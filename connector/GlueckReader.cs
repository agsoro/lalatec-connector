// GlueckReader.cs – Glück controller driver (Modbus TCP)
//
//  Register map  (0-based, float32 = 2 × 16-bit registers)
//
//    Address  Telemetry key    Unit   Notes
//    ───────  ───────────────  ─────  ──────────────────
//    0x1230   power_limit_pct  %      0.0 – 100.0
//    (= 4656 decimal)

using System;
using System.Collections.Generic;

namespace Connector
{
    using Telemetry = Dictionary<string, object>;

    class GlueckReader : IDeviceReader
    {
        const ushort REG_ACTIVE_ALLOWED_POWER_PCT = 0x1230; // 4656 decimal

        public string DriverName => "Glück";

        // =====================================================================
        //  IDeviceReader.Read
        // =====================================================================
        public Telemetry Read(ConnectionConfig conn, DeviceConfig device)
        {
            byte slaveId = device.SlaveId
                ?? throw new InvalidOperationException($"Device '{device.Name}' is missing slaveId.");

            return ModbusHelper.WithMaster(conn, master =>
            {
                float pct = ModbusHelper.ReadFloat32(master, slaveId, REG_ACTIVE_ALLOWED_POWER_PCT);

                return new Telemetry
                {
                    [TelemetryKeys.PowerLimitPct] = Math.Round(pct, 2),
                };
            });
        }
    }
}
