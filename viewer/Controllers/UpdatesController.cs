using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using viewer.Hubs;
using viewer.Models;

using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;

namespace viewer.Controllers
{
    [Route("api/[controller]")]
    public class UpdatesController : Controller
    {
        #region Data Members

        private bool EventTypeSubcriptionValidation
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "SubscriptionValidation";

        private bool EventTypeNotification
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "Notification";

        private readonly IHubContext<GridEventsHub> _hubContext;

        private VssConnection _connection;

        #endregion

        #region Constructors

        public UpdatesController(IHubContext<GridEventsHub> gridEventsHubContext)
        {
            this._hubContext = gridEventsHubContext;
            InitAzureDevOps();
        }

        #endregion

        #region Public Methods

        [HttpOptions]
        public async Task<IActionResult> Options()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var webhookRequestOrigin = HttpContext.Request.Headers["WebHook-Request-Origin"].FirstOrDefault();
                var webhookRequestCallback = HttpContext.Request.Headers["WebHook-Request-Callback"];
                var webhookRequestRate = HttpContext.Request.Headers["WebHook-Request-Rate"];
                HttpContext.Response.Headers.Add("WebHook-Allowed-Rate", "*");
                HttpContext.Response.Headers.Add("WebHook-Allowed-Origin", webhookRequestOrigin);
            }

            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> Post()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var jsonContent = await reader.ReadToEndAsync();

                // Check the event type.
                // Return the validation code if it's 
                // a subscription validation request. 
                if (EventTypeSubcriptionValidation)
                {
                    return await HandleValidation(jsonContent);
                }
                else if (EventTypeNotification)
                {
                    // Check to see if this is passed in using
                    // the CloudEvents schema
                    if (IsCloudEvent(jsonContent))
                    {
                        return await HandleCloudEvent(jsonContent);
                    }

                    // We'll kick off build here to test

                    SampleBuildREST();

                    return await HandleGridEvents(jsonContent);
                }

                return BadRequest();                
            }
        }

        #endregion

        #region Private Methods

        async void InitAzureDevOps()
        {
            var collectionUri = "https://dev.azure.com/chdurham2020/";
            string PAT = Environment.GetEnvironmentVariable("PAT");

            // Create connection
            _connection = new VssConnection(new Uri(collectionUri), new VssBasicCredential(string.Empty, PAT));
            await _connection.ConnectAsync();
        }

        private void SampleBuildREST()
        {
            BuildHttpClient buildClient = _connection.GetClient<BuildHttpClient>();
            ProjectHttpClient projectClient = _connection.GetClient<ProjectHttpClient>();

            var teamProjectName = "JSK-VM";
            var buildDefId = 23;

            var buildDefinition = buildClient.GetDefinitionAsync(teamProjectName, buildDefId).Result;
            Console.WriteLine(buildDefinition.Name);

            var teamProject = projectClient.GetProject(teamProjectName).Result;
            Console.WriteLine(projectClient.BaseAddress);

            try
            {
                buildClient.QueueBuildAsync(new Build() { Definition = buildDefinition, Project = teamProject, SourceBranch = "refs/heads/v0.4.1branch" }).Wait();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }


        private async Task<JsonResult> HandleValidation(string jsonContent)
        {
            var gridEvent =
                JsonConvert.DeserializeObject<List<GridEvent<Dictionary<string, string>>>>(jsonContent)
                    .First();

            await this._hubContext.Clients.All.SendAsync(
                "gridupdate",
                gridEvent.Id,
                gridEvent.EventType,
                gridEvent.Subject,
                gridEvent.EventTime.ToLongTimeString(),
                jsonContent.ToString());

            // Retrieve the validation code and echo back.
            var validationCode = gridEvent.Data["validationCode"];
            return new JsonResult(new
            {
                validationResponse = validationCode
            });
        }

        private async Task<IActionResult> HandleGridEvents(string jsonContent)
        {
            var events = JArray.Parse(jsonContent);
            foreach (var e in events)
            {
                // Invoke a method on the clients for 
                // an event grid notiification.                        
                var details = JsonConvert.DeserializeObject<GridEvent<dynamic>>(e.ToString());

                if(details.EventType == "Microsoft.Storage.BlobCreated" && _connection.HasAuthenticated)
                {
                    await this._hubContext.Clients.All.SendAsync(
                        "gridupdate",
                        details.Id,
                        "True",
                        details.Subject,
                        details.EventTime.ToLongTimeString(),
                        e.ToString());
                }
                else
                {
                    await this._hubContext.Clients.All.SendAsync(
                        "gridupdate",
                        details.Id,
                        details.EventType,
                        details.Subject,
                        details.EventTime.ToLongTimeString(),
                        e.ToString());
                }


            }


            return Ok();
        }

        private async Task<IActionResult> HandleCloudEvent(string jsonContent)
        {
            var details = JsonConvert.DeserializeObject<CloudEvent<dynamic>>(jsonContent);
            var eventData = JObject.Parse(jsonContent);

            await this._hubContext.Clients.All.SendAsync(
                "gridupdate",
                details.Id,
                details.Type,
                details.Subject,
                details.Time,
                eventData.ToString()
            );

            return Ok();
        }

        private static bool IsCloudEvent(string jsonContent)
        {
            // Cloud events are sent one at a time, while Grid events
            // are sent in an array. As a result, the JObject.Parse will 
            // fail for Grid events. 
            try
            {
                // Attempt to read one JSON object. 
                var eventData = JObject.Parse(jsonContent);

                // Check for the spec version property.
                var version = eventData["specversion"].Value<string>();
                if (!string.IsNullOrEmpty(version)) return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            return false;
        }

        #endregion
    }
}