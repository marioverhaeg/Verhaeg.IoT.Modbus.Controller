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
                    Log.Error("Modbus message cannot be parsed, ignoring message.");
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
            if (minute % 5 == 0 && (DateTime.Now.Second == 0 || DateTime.Now.Second == 1))
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
            float return_value = 0.0f;
            if (Solar_Main != null && Solar_Tuin != null)
            {
                if (smart_charge_setpoint == 0.0 || ComputeAmps())
                {
                    long production = Solar_Main.active_power_w + (Solar_Tuin.active_power_w * -1);
                    Log.Debug("Current solar production: " + production + "wH.");
                    if (production > 3000)
                    {
                        // Send 8A charge at 3kW
                        // Send 10A charge at 5kW
                        // send 13A charge at 7kW
                        // send 16A charge at 9kW
                        
                        if (production < 8000)
                        {
                            float new_target_setpoint = (Convert.ToSingle(production) / 230 / 3);
                            smart_charge_setpoint = Convert.ToSingle(Math.Ceiling(new_target_setpoint));
                            Log.Debug("Smart charge setpoint set to " + smart_charge_setpoint + "A.");
                            if (smart_charge_setpoint < 8)
                            {
                                Log.Debug("Overriding smart charge setpoint to 8A.");
                                smart_charge_setpoint = 8;
                            }
                            
                            Log.Debug("Adjusting target setpoint to " + smart_charge_setpoint + "A based on current generation of " + production + "W.");
                            return_value = smart_charge_setpoint;
                        }
                        else
                        {
                            Log.Debug("Set maximum setpoint.");
                            return_value = 16;
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
                    return_value = smart_charge_setpoint;
                }
            }
            else
            {
                Log.Debug("Current delivery not sufficient to start car charge.");
                smart_charge_setpoint = 0;
            }

            return return_value;
        }

        // target_aut_a is target charge speed in A per phase.
        private float ComputeAmps(float target_auto_a)
        {
            // p1d is data coming from P1 meter
            float current_max = Convert.ToSingle(new double[] { p1d.current_amps_p1, p1d.current_amps_p3, p1d.current_amps_p2 }.Max());

            Log.Debug("Maximum grid amperare: " + current_max + "A.");
            Log.Debug("Current delivery: " + p1d.current_delivery + "kW.");
            // My grid connection is configured at 23A (by Shell Recharge)
            float max_send = 23.0f;
            float min_send = 16.0f;

            if (car_power != null && p1d != null)
            {
                if (current_max >= 24)
                {
                    Log.Debug("Current drain from grid larger than 24A. Returning " + current_max.ToString() + "A.");
                    return current_max;
                }
                else if (target_auto_a == 0.0)
                {
                    Log.Debug("No charge required at this moment.");
                    return max_send;
                }
                else
                {
                    // car_power data is coming from separate (HomeWizard) meter, connected negative.
                    // Only re-evaluate setpoint if car is charging, otherwise keep setpoint constant.
                    float current_auto_a = Convert.ToSingle(Math.Ceiling(car_power.active_power_l1_w / 230.0));
                    Log.Debug("Car is charging with " + current_auto_a.ToString() + "A per phase.");

                    if (current_max - target_auto_a > 6)
                    {
                        Log.Debug("Power consumption is over 6A at this moment, reducing target charging speed to 10A.");
                        target_auto_a = 10;
                    }

                    float diff = Convert.ToSingle(Math.Round(current_auto_a - target_auto_a, 2));
                    Log.Debug("Difference between current and setpoint is " + diff + "A.");

                    if (Math.Abs(diff) < 0.8f)
                    {
                        // Difference between setpoint and target is less than 0.8A. Keep charging speed stable.
                        Log.Debug("Setpoint within margin; stable charge speed.");
                        csmr_setpoint = 22.0f;
                    }
                    else
                    {
                        Log.Debug("Difference between setpoint and current charge is higher than 0.8A.");
                        if (diff > 0)
                        {
                            Log.Debug("Decreasing charge speed.");
                            csmr_setpoint = max_send;
                        }
                        else
                        {
                            if (current_auto_a == 0)
                            {
                                Log.Debug("Starting car charge.");
                                csmr_setpoint = 2;
                            }
                            else
                            {
                                // Current (A) is below target, send 17A to increase car charging speed.
                                Log.Debug("Increasing charge speed.");
                                csmr_setpoint = min_send;
                            }
                        }
                    }
                    Log.Debug("CSMR setpoint is " + csmr_setpoint + "A.");
                    return csmr_setpoint;
                }
            }
            else
            {
                Log.Debug("No car or solar data available.");
                return Convert.ToSingle(max_send);
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
