// GlueckReader.cs – Glück controller driver (Modbus TCP)
//
//  Register map  (0-based, float32 = 2 × 16-bit registers)
//
//    Address  Telemetry key              Unit   Notes
//    ───────  ─────────────────────────  ─────  ──────────────────
//    0x1230   active_allowed_power_pct   %      0.0 – 100.0
//    (= 4656 decimal)
//
//  To expose additional registers add them to connector.json:
//    "writableRegisters": [
//      { "key": "active_allowed_power_pct", "address": "0x1230", "type": "float32-be" }
//    ]

using System;
using System.Collections.Generic;
using System.Linq;

namespace Connector
{
    using Telemetry = Dictionary<string, double>;

    class GlueckReader : IDeviceReader, IDeviceWriter
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
                    [TelemetryKeys.PowerAllowedPct] = Math.Round(pct, 2),
                };
            });
        }

        // =====================================================================
        //  IDeviceWriter.Write
        // =====================================================================
        public void Write(ConnectionConfig conn, DeviceConfig device, string key, double value)
        {
            if (device.WritableRegisters is not { Count: > 0 })
                throw new InvalidOperationException(
                    $"Device '{device.Name}' has no 'writableRegisters' configured in connector.json.");

            var reg = device.WritableRegisters
                .FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException(
                    $"Key '{key}' is not listed in 'writableRegisters' for device '{device.Name}'.");

            byte slaveId = device.SlaveId
                ?? throw new InvalidOperationException($"Device '{device.Name}' is missing slaveId.");

            // Parse address – accepts decimal ("4656") and hex ("0x1230")
            ushort address = reg.Address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt16(reg.Address, 16)
                : ushort.Parse(reg.Address);

            ModbusHelper.WithMaster(conn, master =>
            {
                ModbusHelper.WriteRegister(master, slaveId, address, reg.Type, value);
                return 0;   // Func<T> requires a return value
            });
        }
    }
}
