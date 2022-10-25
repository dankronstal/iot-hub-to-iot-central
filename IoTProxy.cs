using IoTHubTrigger = Microsoft.Azure.WebJobs.EventHubTriggerAttribute;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.EventHubs;
using System.Text;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using Microsoft.Azure.Devices.Shared;
using System.Threading;
using System.Threading.Tasks; 

namespace Insight.IoTProxy
{
    internal enum StatusCode
    {
        Completed = 200,
        InProgress = 202,
        ReportDeviceInitialProperty = 203,
        BadRequest = 400,
        NotFound = 404
    }
    public class IoTProxy
    {
        const string MODEL_ID = "<-- get from Device Template DTDL ('@id' value) -->";
        const string IOTHUB_DEVICE_SECURITY_TYPE = "dps"; //hardcoded to 'dps'' for IoT Central

        //Ideally we'd pass these parameteres in, or hydrate from a keyvault or something
        Parameters p = new Parameters(){
            DeviceSecurityType = "dps",
            PrimaryConnectionString = "", //unused for this app, since we're leveraging DPS connectivity as required by IoT Central
            DpsEndpoint = "global.azure-devices-provisioning.net",
            DpsIdScope = "<-- Device connection groups field: ID scope -->",
            DeviceId = "<-- Device connection groups field: Device ID -->",
            DeviceSymmetricKey = "<-- Device connection groups field: Primary key -->",
        };

        private static HttpClient client = new HttpClient();
        
        [FunctionName("IoTProxy")]
        public async Task Run([IoTHubTrigger("messages/events", Connection = "CONN_EVENTHUB")]EventData data, ILogger log)
        {
            log.LogInformation($"C# IoT Hub trigger function is processing a message");

            string messageBody = $"{Encoding.UTF8.GetString(data.Body)}";
            string convertedPayload = "";
            string convertedPayloadMetrics = "";

            if(messageBody.IndexOf("payload")<0){
                //we might be testing or something, since the 'payload' component of the expected message was not included
                //therefore we'll use a static test message.
                messageBody = "copy sample here from the provided file: /Samples and Templates/PayloadFromIgnition.json, or cook your own sample";
            }else{
                Reading r = null;
                try{
                    r = JsonConvert.DeserializeObject<Reading>(messageBody);
                    foreach(var m in r.payload.metrics){
                        convertedPayloadMetrics += $"{{\"{m.name.Replace("/","_").Trim()}\": {m.value}}},";
                    }
                    r.payload.metrics = new List<Metric>();
                    convertedPayload = JsonConvert.SerializeObject(r).Replace("\"metrics\":[]","\"metrics\":["+convertedPayloadMetrics.TrimEnd(',')+"]");
                    log.LogInformation("new payload is: "+convertedPayload);
                }catch(Exception e){
                    log.LogError(e.Message);
                    log.LogError("Oops, there was a deserialization issue...");
                    log.LogError(messageBody);
                }
            }

            try
            {
                CancellationTokenSource cts = new CancellationTokenSource();
                using DeviceClient deviceClient = await SetupDeviceClientAsync(p, log, cts.Token);

                using var message = new Message(Encoding.UTF8.GetBytes(convertedPayload))
                {
                    ContentEncoding = "utf-8",
                    ContentType = "application/json",
                };

                await deviceClient.SendEventAsync(message, cts.Token);
                await deviceClient.CloseAsync();
            }
            catch (Exception e) {
                log.LogError(e.Message);
                log.LogError("Oops, there was a message delivery issue...");
             }
        }

        private static async Task<DeviceClient> SetupDeviceClientAsync(Parameters parameters, ILogger logger, CancellationToken cancellationToken)
        {
            logger.LogDebug($"Initializing via DPS");
            DeviceRegistrationResult dpsRegistrationResult = await ProvisionDeviceAsync(parameters, cancellationToken);
            var authMethod = new DeviceAuthenticationWithRegistrySymmetricKey(dpsRegistrationResult.DeviceId, parameters.DeviceSymmetricKey);
            return InitializeDeviceClient(dpsRegistrationResult.AssignedHub, authMethod);
        }

        // Provision a device via DPS, by sending the PnP model Id as DPS payload.
        private static async Task<DeviceRegistrationResult> ProvisionDeviceAsync(Parameters parameters, CancellationToken cancellationToken)
        {
            using SecurityProvider symmetricKeyProvider = new SecurityProviderSymmetricKey(parameters.DeviceId, parameters.DeviceSymmetricKey, null);
            using ProvisioningTransportHandler mqttTransportHandler = new ProvisioningTransportHandlerMqtt();
            ProvisioningDeviceClient pdc = ProvisioningDeviceClient.Create(parameters.DpsEndpoint, parameters.DpsIdScope,
                symmetricKeyProvider, mqttTransportHandler);

            var pnpPayload = new ProvisioningRegistrationAdditionalData
            {
                JsonData = $"{{ \"modelId\": \"{MODEL_ID}\" }}",
            };

            return await pdc.RegisterAsync(pnpPayload, cancellationToken);
        }

        // Initialize the device client instance using symmetric key based authentication, over Mqtt protocol (TCP, with fallback over Websocket) and setting the ModelId into ClientOptions.
        private static DeviceClient InitializeDeviceClient(string hostname, IAuthenticationMethod authenticationMethod)
        {
            var options = new ClientOptions
            {
                ModelId = MODEL_ID
            };

            return DeviceClient.Create(hostname, authenticationMethod, Microsoft.Azure.Devices.Client.TransportType.Mqtt, options);
        }
    }

    internal class Parameters
    {
        public string DeviceSecurityType { get; set; }
        public string PrimaryConnectionString { get; set; }
        public string DpsEndpoint { get; set; }
        public string DpsIdScope { get; set; }
        public string DeviceId { get; set; }
        public string DeviceSymmetricKey { get; set; }
        public double? ApplicationRunningTime { get; set; }
    }

    internal class Metric
    {
        public string name { get; set; }
        public object timestamp { get; set; }
        public string dataType { get; set; }
        public object value { get; set; }
    }

    internal class Payload
    {
        public long timestamp { get; set; }
        public List<Metric> metrics { get; set; }
        public int seq { get; set; }
    }

    internal class Reading
    {
        public Topic topic { get; set; }
        public Payload payload { get; set; }
    }

    internal class Topic
    {
        [JsonProperty(PropertyName = "namespace")]
        public string ns { get; set; }
        public string edgeNodeDescriptor { get; set; }
        public string groupId { get; set; }
        public string edgeNodeId { get; set; }
        public string deviceId { get; set; }
        public string type { get; set; }
    }
}