using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verhaeg.IoT.Fields.Appliances.EMS;
using Verhaeg.IoT.Fields.Energy.DSMR;
using Verhaeg.IoT.Fields.Energy.Xemex;
using Verhaeg.IoT.Fields.Modbus;
using Verhaeg.IoT.Processor;
using Verhaeg.IoT.RS485.Client;
using Vibrant.InfluxDB.Client;
using Newtonsoft.Json;
using Verhaeg.IoT.Fields.Energy.Meter;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using Verhaeg.IoT.Ditto;
using Verhaeg.IoT.Ditto.Api20;
using Verhaeg.IoT.Fields.Appliances.Car;

namespace Verhaeg.IoT.Modbus.Controller.Managers
{
    public class CSMRManager : QueueManager
    {
        // SingleTon
        private static CSMRManager? _instance = null;
        private static readonly object padlock = new object();
        private static readonly object update = new object();

        // Start value
        private float csmr_setpoint;
        private int ditto_index;
        private float smart_charge_setpoint;

        // Values
        private P1_data p1d;
        private Power car_power;
        private Power Solar_Main;
        private Power Solar_Tuin;
        private ChargingProgram cp;

        public static CSMRManager Instance()
        {
            lock (padlock)
            {
                if (_instance == null)
                {
                    _instance = new CSMRManager("CSMRManager");
                    return _instance;
                }
                else
                {
                    return (CSMRManager)_instance;
                }
            }
        }

        private CSMRManager(string name) : base(name)
        {
            csmr_setpoint = 22.0f;
            ditto_index = 0;
            smart_charge_setpoint = 0f;
            Solar_Main = new Power("", 0, 0, 0, 0);
            Solar_Tuin = new Power("", 0, 0, 0, 0);

            Thing t = DittoManager.Instance().GetThing("Verhaeg.IoT.CSMB.ChargingProgram:Enyaq");
            cp = new ChargingProgram(t);
        }

        public void UpdateValues(Fields.MQTT.Message e)
        {
            lock (update)
            {
                try
                {
                    switch (e.topic)
                    {
                        case "RP120/DSMR":
                            p1d = JsonConvert.DeserializeObject<P1_data>(e.payload);
                            break;
                        case "RP120/Power/Verhaeg.IoT.Energy.Meter:Auto":
                            car_power = JsonConvert.DeserializeObject<Power>(e.payload);
                            break;
                        case "RP120/Power/Verhaeg.IoT.Energy.Meter:Solar.Tuin":
                            Solar_Tuin = JsonConvert.DeserializeObject<Power>(e.payload);
                            break;
                        case "RP120/Power/Verhaeg.IoT.Energy.Meter:Solar":
                            Solar_Main = JsonConvert.DeserializeObject<Power>(e.payload);
                            break;
                        default:
                            break;
                    }
                    
                }
                catch (Exception ex)
                {
                    Log.Error("Could not update values.");
                    Log.Error(ex.ToString());
                }
            }
        }

        protected override void Dispose()
        {

        }

        protected override void Process(object obj)
        {
            lock (update)
            {
                InterpretModbusMessage((string)obj);
            }
        }

        private void InterpretModbusMessage(string message)
        {
            try
            {
                if (message.StartsWith("0103500C"))
                {
                    Log.Debug("Index " + ditto_index + ", no need to updata data from Ditto.");
                    if (ditto_index == 30)
                    {
                        Log.Debug("Index = 30, updating data from Ditto.");
                        Thing t = DittoManager.Instance().GetThing("Verhaeg.IoT.CSMB.ChargingProgram:Enyaq");
                        cp = new ChargingProgram(t);
                        Log.Debug("Resetting index.");
                        ditto_index = 0;
                    }
                    ditto_index++;
                    
                    float setpoint = 0.0f;
                    if (cp != null)
                    {
                        if (cp.charging_speed != 0)
                        {
                            // Execute charging program.
                            setpoint = ComputeAmps(Convert.ToSingle(cp.charging_speed));
                        }
                        else
                        {
                            // Check if smart (solar) charging can be used.
                            setpoint = ComputeAmps(ComputeSolarAmps());
                        }
                    }
                    else
                    {
                        // Check if smart (solar) charging can be used.
                        setpoint = ComputeAmps(ComputeSolarAmps());
                    }

                    Request req = new Request(message);

                    Log.Debug("Sending: " + setpoint.ToString() + "A");
                    Amperage xa = new Amperage("Verhaeg.IoT.Energy:Xemex.Amperage", setpoint, setpoint, setpoint);
                    Response res = GenerateResponseFromAmperage(xa);
                    Client.Instance().Send(res.raw_message);
                }
                else if (message.StartsWith("01034002"))
                {
                    string response = "010302514205E500";
                    byte[] br = Convert.FromHexString(response);
                    Client.Instance().Send(br);
                }
                else if (message.StartsWith("01030C"))
                {
                    Log.Debug("Received response for power data; this should only happen when de CSMB is connected to Modbus.");
                    Response res = new Response(message);
                    Amperage xa = new Fields.Energy.Xemex.Amperage("Verhaeg.IoT.Energy:Xemex.Amperage", res);
                    Response res_test = GenerateResponseFromAmperage(xa);
                    Log.Debug("Generated TEST response: " + res_test.message);
                }
                else if (message.StartsWith("00"))
                {
                    //Log.Debug("Received broadcast message, ignoring message.");
                }
                else
                {
                    Log.Debug("Modbus message cannot be parsed, ignoring message.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not parse response.");
                Log.Debug(ex.ToString());
            }
        }

        private bool ComputeAmps()
        {
            int minute = DateTime.Now.Minute;
            if (minute % 3 == 0 && (DateTime.Now.Second == 0 || DateTime.Now.Second == 1))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private float ComputeSolarAmps()
        {
            if (Solar_Main != null && Solar_Tuin != null)
            {
                if (smart_charge_setpoint == 0.0 || ComputeAmps())
                {
                    long production = Solar_Main.active_power_w + (Solar_Tuin.active_power_w * -1);
                    Log.Debug("Current solar production: " + production + "wH.");
                    if (production > 4000)
                    {                        
                        if (production < 9000)
                        {
                            float new_target_setpoint = (Convert.ToSingle(production) / 230 / 3);
                            smart_charge_setpoint = Convert.ToSingle(Math.Ceiling(new_target_setpoint));
                            Log.Debug("Smart charge setpoint set to " + smart_charge_setpoint + "A.");
                            if (smart_charge_setpoint < 8f)
                            {
                                Log.Debug("Overriding smart charge setpoint to 8A.");
                                smart_charge_setpoint = 8f;
                            }
                            Log.Debug("Adjusting target setpoint to " + smart_charge_setpoint + "A based on current generation of " + production + "W.");
                        }
                        else
                        {
                            Log.Debug("Set maximum setpoint.");
                            smart_charge_setpoint = 16.0f;
                        }

                        if (p1d.current_usage > 2000 && smart_charge_setpoint > 8f)
                        {
                            Log.Debug("Reducing smart_charge_setpoint with " + (Convert.ToSingle(p1d.current_usage) / 230 + "A."));
                            smart_charge_setpoint = smart_charge_setpoint + 1000 - (Convert.ToSingle(p1d.current_usage) / 230);
                        }
                    }
                    else
                    {
                        Log.Debug("Current delivery not sufficient to start car charge.");
                        smart_charge_setpoint = 0;
                    }
                }
                else
                {
                    Log.Debug("Do not change smart charge value, currently set to " + smart_charge_setpoint + "A.");
                }
            }
            else
            {
                Log.Debug("Current delivery not sufficient to start car charge.");
                smart_charge_setpoint = 0;
            }

            return smart_charge_setpoint;
        }

        // target_aut_a is target charge speed in A per phase.
        private float ComputeAmps(float target_auto_a)
        {
            // p1d is data coming from P1 meter
            float current_max = Convert.ToSingle(new double[] { p1d.current_amps_p1, p1d.current_amps_p3, p1d.current_amps_p2 }.Max());

            Log.Debug("Maximum grid fase draw: " + current_max + "A.");
            Log.Debug("Charge target: " + target_auto_a + "A.");
            // My grid connection is configured at 23A (by Shell Recharge)
            float max_send = 20.5f;
            float min_send = 10.0f;

            // car_power data is coming from separate (HomeWizard) meter, connected negative.
            // Only re-evaluate setpoint if car is charging, otherwise keep setpoint constant.
            float current_auto_a = 0.0f;
            float current_rest = 0.0f;
            if (car_power != null)
            {
                current_auto_a = Convert.ToSingle(Math.Round(car_power.active_power_l1_w / 230.0 * -1,2));
                Log.Debug("Car is charging with " + current_auto_a.ToString() + "A per phase.");
                current_rest = current_max - current_auto_a;
                Log.Debug("Other grid fase draw: " + current_rest + "A per phase.");
            }

            // TESTING
            // target_auto_a = 7.0f;
            // TESTING

            if (car_power != null && p1d != null && target_auto_a != 0)
            {
                // Risk on fase overload.
                if (current_max >= 24 && p1d.current_usage > 13000)
                {
                    target_auto_a = max_send - current_rest;
                    if (target_auto_a < 8)
                    {
                        target_auto_a = 0f;
                    }
                    Log.Error("Charge target: " + target_auto_a + "A.");
                    Log.Error("Current drain: " + p1d.current_usage + "wH.");
                    Log.Error("Current max: " + current_max + "A.");
                    Log.Error("Current drain from grid larger than 24A and consumption larger than 13kW. Reduce current target to " + target_auto_a + "A.");
                }
                else if (target_auto_a + current_rest > max_send)
                {
                    target_auto_a = max_send - current_rest;
                    Log.Error("Charge target: " + target_auto_a + "A.");
                    Log.Error("Current drain: " + p1d.current_usage + "wH.");
                    Log.Error("Current max: " + current_rest + "wH.");
                    Log.Debug("Charge target + rest consumption > max_send. Reduce current target to " + target_auto_a + "A.");
                }

                return ComputeSetpoint(target_auto_a, current_auto_a, max_send);
            }
            else
            {
                Log.Debug("No car or solar data available.");
                return 24.0f;
            }

        }

        public float ComputeSetpoint(float target_charge_a, float current_auto_a, float max_send)
        {
            float diff = Convert.ToSingle(Math.Round(current_auto_a - target_charge_a, 2));
            Log.Debug("Difference between current and setpoint is " + diff + "A.");
            if (current_auto_a < 1)
            {
                return 12.0f;
            }
            else if (Math.Abs(diff) < 1)
            {
                return max_send;
            }
            else if (diff > 0)
            {
                return (diff * 0.1f) + max_send;
            }
            else
            {
                return max_send - target_charge_a + current_auto_a;
            }
            
        }

        public void DebugAmperage(Amperage xa)
        {
            Log.Debug("l1: " + xa.active_power_l1_a.ToString() + "A");
            Log.Debug("l2: " + xa.active_power_l2_a.ToString() + "A");
            Log.Debug("l3: " + xa.active_power_l3_a.ToString() + "A");
        }
        
        public Response GenerateResponseFromAmperage(Amperage ax)
        {
            List<float> lData = new List<float>();
            lData.Add(ax.active_power_l1_a);
            lData.Add(ax.active_power_l2_a);
            lData.Add(ax.active_power_l3_a);

            string data = GetHexFromData(ax.active_power_l1_a) + GetHexFromData(ax.active_power_l2_a) + GetHexFromData(ax.active_power_l3_a);
            Response res = new Response(12, data, lData);
            return res;
        }

        private string GetHexFromData(float data)
        {
            byte[] b = BitConverter.GetBytes(data);
            //Array.Reverse(b);
            uint ui = BitConverter.ToUInt32(b, 0);
            string str = ui.ToString("X8");
            return str;
        }
    }
}
