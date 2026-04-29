// SunSpecReader.cs – SunSpec compliant device driver (Modbus TCP)
//
//  This driver performs discovery of SunSpec models by walking the model chain
//  starting from common base addresses (40000, 0, or 50000).
//  It currently extracts power and energy values from Inverter (101-103)
//  and Meter (201-204) models, applying the appropriate scale factors.
//

using System;
using System.Collections.Generic;
using NModbus;

namespace Connector
{
    using Telemetry = Dictionary<string, double>;

    class SunSpecReader : IDeviceReader
    {
        public string DriverName => "SunSpec";

        public Telemetry Read(ConnectionConfig conn, DeviceConfig device)
        {
            byte slaveId = device.SlaveId
                ?? throw new InvalidOperationException($"Device '{device.Name}' is missing slaveId.");

            return ModbusHelper.WithMaster(conn, master =>
            {
                var telemetry = new Telemetry();

                // 1. Find base address (Commonly 40000, 0, or 50000)
                ushort? baseAddr = FindBaseAddress(master, slaveId);
                if (baseAddr == null)
                    throw new Exception($"Could not find SunSpec 'SunS' marker on {device.Name} at 40000, 0, or 50000.");

                // 2. Walk models
                ushort currentAddr = (ushort)(baseAddr.Value + 2); // skip "SunS" marker
                int safetyCounter = 0;

                while (safetyCounter++ < 50) // limit to 50 models per device
                {
                    var header = master.ReadHoldingRegisters(slaveId, currentAddr, 2);
                    ushort modelId = header[0];
                    ushort length = header[1];

                    if (modelId == 0xFFFF || modelId == 0) break; // End of map

                    ProcessModel(master, slaveId, currentAddr, modelId, length, telemetry);

                    currentAddr += (ushort)(2 + length);
                    if (currentAddr >= 65530) break;
                }

                return telemetry;
            });
        }

        private ushort? FindBaseAddress(IModbusMaster master, byte slaveId)
        {
            ushort[] candidates = { 40000, 0, 50000 };
            foreach (var addr in candidates)
            {
                try
                {
                    var regs = master.ReadHoldingRegisters(slaveId, addr, 2);
                    // "SunS" marker: 0x5375, 0x6E53
                    if (regs[0] == 0x5375 && regs[1] == 0x6E53) return addr;
                }
                catch { /* skip to next candidate */ }
            }
            return null;
        }

        private void ProcessModel(IModbusMaster master, byte slaveId, ushort startAddr, ushort modelId, ushort length, Telemetry telemetry)
        {
            // We care about Inverter (101, 102, 103) and Meter (201, 202, 203, 204)
            if (modelId >= 101 && modelId <= 103)
            {
                ReadInverterModel(master, slaveId, startAddr, length, telemetry);
            }
            else if (modelId >= 201 && modelId <= 204)
            {
                ReadMeterModel(master, slaveId, startAddr, length, telemetry);
            }
        }

        private void ReadInverterModel(IModbusMaster master, byte slaveId, ushort startAddr, ushort length, Telemetry telemetry)
        {
            // Model 101/103 relative offsets (after 2-reg header):
            //   W (Watts): 14
            //   W_SF: 15
            //   WH (Watt-hours): 24 (uint32)
            //   WH_SF: 26
            var data = master.ReadHoldingRegisters(slaveId, (ushort)(startAddr + 2), Math.Min(length, (ushort)30));

            if (data.Length >= 16)
            {
                short w = (short)data[14];
                short w_sf = (short)data[15];
                if (w != -32768) // SunSpec "Not Implemented" value
                    telemetry[TelemetryKeys.PowerKw] = Math.Round(w * Math.Pow(10, w_sf) / 1000.0, 3);
            }

            if (data.Length >= 27)
            {
                uint wh = (uint)(data[24] << 16 | data[25]);
                short wh_sf = (short)data[26];
                if (wh != 0xFFFFFFFF)
                    telemetry[TelemetryKeys.EnergyExportKwh] = Math.Round(wh * Math.Pow(10, wh_sf) / 1000.0, 3);
            }
        }

        private void ReadMeterModel(IModbusMaster master, byte slaveId, ushort startAddr, ushort length, Telemetry telemetry)
        {
            // Model 201-204 (Meter) relative offsets (after 2-reg header):
            //   W: 18
            //   W_SF: 19
            //   TotWhExp: 32 (uint32)
            //   TotWhImp: 40 (uint32)
            //   TotWh_SF: 48
            var data = master.ReadHoldingRegisters(slaveId, (ushort)(startAddr + 2), Math.Min(length, (ushort)50));

            if (data.Length >= 20)
            {
                short w = (short)data[18];
                short w_sf = (short)data[19];
                if (w != -32768)
                    telemetry[TelemetryKeys.PowerKw] = Math.Round(w * Math.Pow(10, w_sf) / 1000.0, 3);
            }

            if (data.Length >= 49)
            {
                uint whImp = (uint)(data[40] << 16 | data[41]);
                uint whExp = (uint)(data[32] << 16 | data[33]);
                short wh_sf = (short)data[48];

                if (whImp != 0xFFFFFFFF)
                    telemetry[TelemetryKeys.EnergyImportKwh] = Math.Round(whImp * Math.Pow(10, wh_sf) / 1000.0, 3);
                if (whExp != 0xFFFFFFFF)
                    telemetry[TelemetryKeys.EnergyExportKwh] = Math.Round(whExp * Math.Pow(10, wh_sf) / 1000.0, 3);
            }
        }
    }
}
