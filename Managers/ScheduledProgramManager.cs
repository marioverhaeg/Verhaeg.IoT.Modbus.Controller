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
using Verhaeg.IoT.Ditto.Api20;
using Verhaeg.IoT.Fields.Appliances.Car;
using Verhaeg.IoT.Fields.Energy;
using Verhaeg.IoT.Ditto;
using System.Runtime.ConstrainedExecution;
using System.Runtime.Intrinsics.X86;

namespace Verhaeg.IoT.Modbus.Controller.Managers
{
    public class ScheduledProgramManager : QueueManager
    {
        // SingleTon
        private static ScheduledProgramManager? _instance = null;
        private static readonly object padlock = new object();
        
        // Start value
        private float csmr_setpoint;

        public static ScheduledProgramManager Instance()
        {
            lock (padlock)
            {
                if (_instance == null)
                {
                    _instance = new ScheduledProgramManager("ScheduledProgramManager");
                    return _instance;
                }
                else
                {
                    return (ScheduledProgramManager)_instance;
                }
            }
        }

        private ScheduledProgramManager(string name) : base(name)
        {
            csmr_setpoint = 0;
        }

        

        protected override void Dispose()
        {

        }

        protected override void Process(object obj)
        {
            Thing t = DittoManager.Instance().GetThing("Verhaeg.IoT.Energy.ScheduledProgram:Car.Charge");
            if (t != null)
            {
                Fields.Energy.ScheduledProgram spe = new Fields.Energy.ScheduledProgram(t);
                if (spe.program_state == "cancelled")
                {
                    Log.Information("Program cancelled.");
                    ChargingProgram cp = new ChargingProgram(0.0);
                    cp.UpdateDigitalTwin();
                }
                else
                {
                    if (spe != null && spe.planned_program_consumption != null)
                    {
                        if (DateTime.UtcNow.Minute < 30)
                        {
                            DateTime dt = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, 0, DateTimeKind.Utc);
                            SetCSMRSetpoint(dt, spe.planned_program_consumption);
                        }
                        else
                        {
                            DateTime dt = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 30, 0, 0, DateTimeKind.Utc);
                            SetCSMRSetpoint(dt, spe.planned_program_consumption);
                        }
                    }
                }
            }
            else
            {
                Log.Error("Could not find Verhaeg.IoT.Energy.Scheduled:Car.Charge, assuming no program is planned.");
                ChargingProgram cp = new ChargingProgram(0.0);
                //cp.UpdateDigitalTwin();
            }
        }

        private void SetCSMRSetpoint(DateTime dt, List<ProgramConsumption> lpc)
        {
            ProgramConsumption? pc = lpc.Where(x => x.timestamp == dt).FirstOrDefault();
            if (pc != null)
            {
                Log.Debug("Found matching timestamp in ScheduledProgram, starting/continuoing charge.");
                double new_csmr_setpoint = Math.Ceiling(pc.max / 230 / 3);
                Log.Debug("New CSMR setpoint: " + new_csmr_setpoint + "A (per phase).");
                Log.Debug("Current CSMR setpoint: " + csmr_setpoint + "A (per phase).");

                if (csmr_setpoint != new_csmr_setpoint)
                {
                    Log.Information("New CSMR setpoints differs from current CSMR setpoint. Updating setpoint to " + new_csmr_setpoint + "A (per phase).");
                    ChargingProgram cp = new ChargingProgram(new_csmr_setpoint);
                    cp.UpdateDigitalTwin();
                }
            }
            else
            {
                Log.Debug("Could not find timestamp " + dt.ToLocalTime().ToString() + " in ScheduledProgram, stopping current Charge.");
                ChargingProgram cp = new ChargingProgram(0.0);
                cp.UpdateDigitalTwin();
            }
        }
    }
}
