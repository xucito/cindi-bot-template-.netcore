﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cindi.DotNetCore.BotExtensions.Exceptions;
using Cindi.DotNetCore.BotExtensions.Requests;
using Cindi.Domain.Entities.Steps;
using Cindi.Domain.Entities.StepTemplates;
using Newtonsoft.Json.Linq;

namespace Cindi.DotNetCore.BotExtensions
{
    public abstract class WorkerBotHandler<TOptions> where TOptions : WorkerBotHandlerOptions
    {
        private readonly string nodeUrl;
        private HttpClient _client;
        Thread serviceThread;
        private bool started = false;
        private object threadLocker = new Object();
        private int waitTime = 1000;
        private object waitTimeLocker = new Object();
        private bool _hasValidHttpClient = false;
        public ILogger Logger;
        protected UrlEncoder UrlEncoder { get; }
        public TOptions Options { get; }
        public int loopNumber = 0;
        public string Id { get; }
        public string RunTime { get; }

        public WorkerBotHandler(IOptionsMonitor<TOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        {
            if (options.CurrentValue.NodeURL != null && options.CurrentValue.NodeURL != "")
            {
                this.nodeUrl = options.CurrentValue.NodeURL;
                _client = new HttpClient();
                _client.BaseAddress = new Uri(this.nodeUrl + "/api");
                _hasValidHttpClient = true;
            }

            Options = options.CurrentValue;

            waitTime = options.CurrentValue.SleepTime;

            // Register the step template
            foreach (var template in options.CurrentValue.StepTemplateLibrary)
            {
                // Queue all the templates for registration
                QueueTemplateForRegistration(template);
            }

            Logger = logger.CreateLogger(this.GetType().FullName);


            if (options.CurrentValue.Id == null)
            {
                Random rnd = new Random();
                Id = BotUtility.GenerateName(rnd.Next(4, 10)) + '-' + (rnd.Next(1, 100));
            }

            // Create a new Run Time Id
            RunTime = Guid.NewGuid().ToString();

            if (Options.AutoStart)
            {
                // Initiate the registration of all templates and run loop if valid
                StartWorking();
            }
        }


        /// <summary>
        /// List of templates this bot can run against
        /// </summary>
        public List<StepTemplate> RegisteredTemplates = new List<StepTemplate>();

        public async void StartWorking()
        {
            await RegisterAllTemplatesAsync();

            if (_hasValidHttpClient)
            {
                Run();
            }
        }

        /// <summary>
        /// Register all templates for processing
        /// </summary>
        /// <returns></returns>
        public async Task<bool> RegisterAllTemplatesAsync()
        {
            foreach (var template in RegisteredTemplates)
            {
                if (_hasValidHttpClient)
                {
                    try
                    {
                        await RegisterTemplateAsync(template);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning(e.Message);
                    }
                }
                else
                {
                    Logger.LogWarning("Could not register template " + template.Id + " as there is no valid httpClient.");
                }
            }
            return true;
        }

        public void QueueTemplateForRegistration(StepTemplate stepTemplate)
        {
            var foundTemplateCount = RegisteredTemplates.Where(rt => rt.Id == stepTemplate.Id).Count();

            if (foundTemplateCount == 0)
            {
                RegisteredTemplates.Add(stepTemplate);
            }
            else if (foundTemplateCount == 1)
            {
                throw new StepTemplateDuplicateFoundException();
            }
            else
            {
                throw new StepTemplateNotFoundException();
            }
        }

        private async Task<bool> RegisterTemplateAsync(StepTemplate stepTemplate)
        {
            var result = await _client.PostAsync(_client.BaseAddress + "/step-templates", new StringContent(JsonConvert.SerializeObject(new NewStepTemplateRequest()
            {
                Name = stepTemplate.Name,
                Version = stepTemplate.Version,
                AllowDynamicInputs = stepTemplate.AllowDynamicInputs,
                InputDefinitions = stepTemplate.InputDefinitions,
                OutputDefinitions = stepTemplate.OutputDefinitions
            }), Encoding.UTF8, "application/json"));

            if (result.IsSuccessStatusCode)
            {
                Logger.LogInformation("Successfully registered template " + stepTemplate.Id);
                return true;
            }
            else
            {
                throw new Exception("Error adding template for template " + stepTemplate.Id);
            }
        }

        public async void Run()
        {
            serviceThread = new Thread(BotLoop);

            started = true;

            // Register all queued the template
            await RegisterAllTemplatesAsync();

            try
            {
                serviceThread.Start();
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        /// <summary>
        /// Used for running the main loop
        /// </summary>
        public async void BotLoop()
        {
            var stopWatch = new Stopwatch();
            while (started)
            {
                Logger.LogInformation("Starting new Thread");
                Step nextStep = null;
                try
                {
                    nextStep = await GetNextStep();
                }
                catch (Exception e)
                {
                    Logger.LogWarning("Error getting next step, will sleep and try again. " + e.Message);
                }
                stopWatch.Reset();
                stopWatch.Start();

                UpdateStepRequest stepResult = new UpdateStepRequest();

                if (nextStep != null)
                {
                    Logger.LogInformation("Processing step " + nextStep.Id);
                    stepResult.Id = nextStep.Id;

                    try
                    {
                        stepResult = await ProcessStep(nextStep);
                    }
                    catch (Exception e)
                    {
                        //If the handler sets the status to error than this does need to be processed

                        stepResult.Status = StepStatuses.Error;
                        stepResult.Logs = "Encountered uncaught error at " + e.Message + ".";/*.Outputs.Add(new CommonData()
                        {
                            Type = (int)CommonData.InputDataType.ErrorMessage,
                            Id = "ErrorMessage",
                            Value = e.Message
                        });*/

                    }

                    int count = 0;
                    bool success = false;
                    while (!success)
                    {
                        try
                        {
                            await _client.PutAsync(_client.BaseAddress + "/Steps/" + nextStep.Id, new StringContent(JsonConvert.SerializeObject(stepResult), Encoding.UTF8, "application/json"));
                            success = true; 
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning("Failed to save step in Cindi with exception " + e.Message + ". Sleeping for 1 seconds and than retrying...");
                            Thread.Sleep(1000);
                        }
                    }
                }
                else
                {
                    Logger.LogInformation("No step found");
                }

                loopNumber++;
                stopWatch.Stop();
                Logger.LogInformation("Completed Service Loop " + loopNumber + " took approximately " + stopWatch.ElapsedMilliseconds + "ms");

                lock (waitTimeLocker)
                {
                    Logger.LogInformation("Sleeping for " + waitTime + "ms");
                    Thread.Sleep(waitTime);
                }
            }
        }

        /// <summary>
        /// Get the next step based on defintions acceptable
        /// </summary>
        /// <returns></returns>
        public async Task<Step> GetNextStep()
        {
            var newRequest = new StepRequest
            {
                StepTemplateIds = RegisteredTemplates.Select(t => t.Id).ToArray()
            };

            var result = await _client.PostAsync(_client.BaseAddress + "/Steps/assignment-requests", new StringContent(JsonConvert.SerializeObject(newRequest), Encoding.UTF8, "application/json"));

            //Get content
            var content = await result.Content.ReadAsStringAsync();

            if (content == "null")
            {
                return null;
            }
           
            //Read the content as a string
            Step step = JObject.Parse(content)["result"].ToObject<Step>();

            return step;
        }

        public async Task<UpdateStepRequest> ProcessStep(Step step)
        {
            try
            {
                if (ValidateStep(step))
                {
                    return await HandleStep(step);
                }
                else
                {
                    Logger.LogError("Unknown error while validating " + step.Id);
                    return null;
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e.Message);
                throw e;
            }
        }


        public bool ValidateStep(Step step)
        {
            var foundStepTemplatesCount = RegisteredTemplates.Where(rt => rt.Id == step.StepTemplateId).Count();

            if (foundStepTemplatesCount == 0)
            {
                throw new StepTemplateNotFoundException("No step templates for step template " + step.StepTemplateId);
            }
            else if (foundStepTemplatesCount == 1)
            {
                return true;
            }
            else
            {
                throw new StepTemplateDuplicateFoundException("Found duplicate step templates for step template " + step.StepTemplateId);
            }
        }

        /// <summary>
        /// This will return the output of a step
        /// </summary>
        /// <param name="step"></param>
        /// <returns></returns>
        public abstract Task<UpdateStepRequest> HandleStep(Step step);
    }
}
