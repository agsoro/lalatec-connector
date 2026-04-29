// AbbReader.cs – ABB energy meter driver (Modbus TCP)
//
//  Register map (based on provided ThingsBoard sample)
//
//    Address  Telemetry key        Type    Objects  Divider
//    ───────  ───────────────────  ──────  ───────  ───────
//    23316    power_kw             32int   2        100000
//    20480    energy_import_kWh    64uint  4        100
//    20484    energy_export_kWh    64uint  4        100
//

using System;
using System.Collections.Generic;

namespace Connector
{
    using Telemetry = Dictionary<string, double>;

    class AbbReader : IDeviceReader
    {
        const ushort REG_POWER_KW = 23316;
        const ushort REG_ENERGY_IMPORT_KWH = 20480;
        const ushort REG_ENERGY_EXPORT_KWH = 20484;

        public string DriverName => "ABB";

        public Telemetry Read(ConnectionConfig conn, DeviceConfig device)
        {
            byte slaveId = device.SlaveId
                ?? throw new InvalidOperationException($"Device '{device.Name}' is missing slaveId.");

            return ModbusHelper.WithMaster(conn, master =>
            {
                // Read 32int (2 registers)
                var powerRegs = master.ReadHoldingRegisters(slaveId, REG_POWER_KW, 2);
                
                // Read 64uint (8 registers total: 4 for import, 4 for export)
                // These are contiguous: 20480-20483 and 20484-20487
                var energyRegs = master.ReadHoldingRegisters(slaveId, REG_ENERGY_IMPORT_KWH, 8);

                return new Telemetry
                {
                    [TelemetryKeys.PowerKw] = Math.Round(ModbusHelper.RegsToInt32(powerRegs, 0) / 100000.0, 3),
                    [TelemetryKeys.EnergyImportKwh] = Math.Round((double)ModbusHelper.RegsToUInt64(energyRegs, 0) / 100.0, 3),
                    [TelemetryKeys.EnergyExportKwh] = Math.Round((double)ModbusHelper.RegsToUInt64(energyRegs, 4) / 100.0, 3),
                };
            });
        }
    }
}
