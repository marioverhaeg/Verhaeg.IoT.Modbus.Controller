﻿using System;
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
using Verhaeg.IoT.Fields.Environment.Meteoserver;
using System.Globalization;
using Verhaeg.IoT.Configuration.Energy;

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
        private double current_usage;
        private double current_amps_p1;
        private double current_amps_p2;
        private double current_amps_p3;


        private Power car_power;
        private Power Solar_Main;
        private Power Solar_Tuin;
        private ChargingProgram cp;
        private Climate_Temperature ct;

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

            Thing t1 = DittoManager.Instance().GetThing("Verhaeg.IoT.CSMB.ChargingProgram:Enyaq");
            cp = new ChargingProgram(t1);

            Thing t2 = DittoManager.Instance().GetThing("Verhaeg.IoT.EMS.Climate:Molenhofweg.Temperature");
            ct = new Climate_Temperature(t2);

            current_usage = -20000;
            current_amps_p1 = 50;
            current_amps_p2 = 50;
            current_amps_p3 = 50;
        }

        public void UpdateValues(Fields.MQTT.Message m)
        {
            lock (update)
            {
                try
                {
                    switch (m.topic)
                    {
                        case "P1/dsmr/reading/electricity_currently_delivered":
                            current_usage = Convert.ToDouble(m.payload, CultureInfo.InvariantCulture);
                            break;
                        case "P1/dsmr/reading/phase_power_current_l1":
                            current_amps_p1 = Convert.ToDouble(m.payload, CultureInfo.InvariantCulture);
                            break;
                        case "P1/dsmr/reading/phase_power_current_l2":
                            current_amps_p2 = Convert.ToDouble(m.payload, CultureInfo.InvariantCulture);
                            break;
                        case "P1/dsmr/reading/phase_power_current_l3":
                            current_amps_p3 = Convert.ToDouble(m.payload, CultureInfo.InvariantCulture);
                            break;
                        case "Homewizard/Power/Verhaeg.IoT.Energy.Meter:Auto":
                            car_power = JsonConvert.DeserializeObject<Power>(m.payload);
                            break;
                        case "Homewizard/Power/Verhaeg.IoT.Energy.Meter:Solar.Tuin":
                            Solar_Tuin = JsonConvert.DeserializeObject<Power>(m.payload);
                            break;
                        case "Homewizard/Power/Verhaeg.IoT.Energy.Meter:Solar":
                            Solar_Main = JsonConvert.DeserializeObject<Power>(m.payload);
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
                        Thing t1 = DittoManager.Instance().GetThing("Verhaeg.IoT.CSMB.ChargingProgram:Enyaq");
                        cp = new ChargingProgram(t1);

                        Thing t2 = DittoManager.Instance().GetThing("Verhaeg.IoT.EMS.Climate:Molenhofweg.Temperature");
                        ct = new Climate_Temperature(t2);

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
                        if (DateTime.Now.ToLocalTime().Hour == 5)
                        {
                            if (ct.outside < 5)
                            {
                                Log.Debug("Outside temperature lower than 5C, heating between 5-6AM.");
                                setpoint = ComputeAmps(Convert.ToSingle(16.0f));
                            }
                        }
                        else
                        {
                            // Check if smart (solar) charging can be used.
                            setpoint = ComputeAmps(ComputeSolarAmps());
                        }
                    
                    }

                    Request req = new Request(message);

                    Log.Debug("Sending: " + setpoint.ToString() + "A");
                    Amperage xa = new Amperage("Verhaeg.IoT.Energy:Xemex.Amperage", setpoint, setpoint, setpoint);
                    Response res = GenerateResponseFromAmperage(xa);
                    TcpClient.Instance().Send(res.raw_message);
                }
                else if (message.StartsWith("01034002"))
                {
                    string response = "010302514205E500";
                    byte[] br = Convert.FromHexString(response);
                    TcpClient.Instance().Send(br);
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
                Log.Error(ex.ToString());
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

                        if (current_usage > 2000 && smart_charge_setpoint > 8f)
                        {
                            Log.Debug("Reducing smart_charge_setpoint with " + (Convert.ToSingle(current_usage) / 230 + "A."));
                            smart_charge_setpoint = smart_charge_setpoint + 1000 - (Convert.ToSingle(current_usage) / 230);
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
            float current_max = Convert.ToSingle(new double[] { current_amps_p1, current_amps_p3, current_amps_p2 }.Max());

            Log.Debug("Maximum grid fase draw: " + current_max + "A.");
            Log.Debug("Planned charge target: " + target_auto_a + "A.");
            // My grid connection is configured at 20A (by Shell Recharge)
            float max_send = 20.0f;
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

            if (car_power != null && current_usage != -20000 && target_auto_a != 0)
            {
                // Risk on fase overload.
                if (current_max >= 24 && current_usage > 13000)
                {
                    target_auto_a = max_send - current_rest;
                    if (target_auto_a < 8)
                    {
                        target_auto_a = 0f;
                    }
                    Log.Debug("Charge target: " + target_auto_a + "A.");
                    Log.Debug("Current drain: " + current_usage + "kWh.");
                    Log.Debug("Current max: " + current_max + "A.");
                    Log.Debug("Current drain from grid larger than 24A and consumption larger than 13kW. Reduce current target to " + target_auto_a + "A.");
                }
                else if (target_auto_a + current_rest > max_send && current_usage > 3680)
                {
                    Log.Debug("Charge target: " + target_auto_a + "A.");
                    Log.Debug("Current drain: " + current_usage + "kWh.");
                    Log.Debug("Current max: " + current_max + "A.");
                    target_auto_a = max_send - current_rest;
                    Log.Debug("Charge target + rest consumption > max_send.");
                }

                if (target_auto_a < 8)
                {
                    target_auto_a = 0;
                     
                }
                Log.Debug("Updated charge target: " + target_auto_a + "A.");
                return ComputeSetpoint(target_auto_a, current_auto_a, max_send);
            }
            else
            {
                if (car_power == null)
                {
                    Log.Debug("Waiting for first car data measurement...");
                }
                if (current_usage == -20000)
                {
                    Log.Debug("Waiting for first P1 data measurement...");
                }
                if (target_auto_a == 0)
                {
                    Log.Debug("Car charge target set to ZERO.");
                }
                return 24.0f;
            }

        }

        public float ComputeSetpoint(float target_charge_a, float current_auto_a, float max_send)
        {
            if (target_charge_a > 8)
            {
                float diff = Convert.ToSingle(Math.Round(current_auto_a - target_charge_a, 2));
                Log.Debug("Difference between current and setpoint is " + diff + "A.");
                if (current_auto_a < 1)
                {
                    Log.Debug("Car not charging, starting car charge.");
                    return 12.0f;
                }
                else if (Math.Abs(diff) < 1)
                {
                    Log.Debug("Difference between target and current is smaller than 1A.");
                    return max_send;
                }
                else if (diff > 0)
                {
                    Log.Debug("Difference between target and current > 0, decreasing charge speed.");
                    return (diff * 0.3f) + max_send;
                }
                else
                {
                    Log.Debug("Increasing charge speed.");
                    return max_send - target_charge_a + current_auto_a;
                }
            }
            else
            {
                Log.Debug("No charge.");
                return 25.0f;
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
