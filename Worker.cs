using Verhaeg.IoT.Configuration.Ditto;
using Verhaeg.IoT.Configuration.MQTT;
using Verhaeg.IoT.Ditto;
using Verhaeg.IoT.Modbus.Controller.Managers;
using Verhaeg.IoT.MQTT.Client.Managers;

namespace Verhaeg.IoT.Modbus.Controller
{
    public class Worker : BackgroundService
    {
        // Logging
        private Serilog.ILogger Log;
        private Mosquito mos_configuration;
        private Configuration.Ditto.HTTP cdh;
        private Configuration.Ditto.WebSocket cdws;

        public Worker(ILogger<Worker> logger)
        {
            Log = Processor.Log.CreateLog("Worker"); 
            mos_configuration = Mosquito.Instance("Mosquito.json");
            cdh = Configuration.Ditto.HTTP.Instance("Ditto_HTTP.json");
            cdws = Configuration.Ditto.WebSocket.Instance("Ditto_WS.json");

        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            DittoManager.Start(cdh.uri.ToString(), cdh.username, cdh.password);
            List<string> lTopics = new List<string>();
            lTopics.Add("Homewizard/Power/#");
            lTopics.Add("P1/dsmr/reading/#");
            EventManager.Start(mos_configuration.ipaddress, mos_configuration.port, mos_configuration.username, mos_configuration.password, lTopics);
            MQTT.Client.Managers.EventManager.Instance().mqtt_event += Worker_mqtt_event;
            State.Time.HalfHour.Instance().TimeEvent += Worker_TimeEvent;

            ScheduledProgramManager.Instance().Write("");

            Thread.Sleep(2000);
            RS485.Client.TcpClient c = RS485.Client.TcpClient.Instance();
            c.DataReceivedEvent += C_DataReceivedEvent;

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);
            }
        }

        private void Worker_TimeEvent(object? sender, KeyValuePair<string, string> e)
        {
            Log.Information("Checking ScheduledProgram for Car.Charge.");
            ScheduledProgramManager.Instance().Write("");
        }

        private void Worker_mqtt_event(object? sender, Fields.MQTT.Message e)
        {
            // Log.Debug("Received message " + e.thingId.ditto_thingId);
            CSMRManager.Instance().UpdateValues(e);
        }

        private void C_DataReceivedEvent(object? sender, string e)
        {
            // Log.Debug("Received data via RS485.");
            CSMRManager.Instance().Write(e);
        }
    }
}