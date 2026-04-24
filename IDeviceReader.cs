// IDeviceReader.cs – common read interface + Modbus TCP helper
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using NModbus;

namespace Connector
{
    using Telemetry = Dictionary<string, double>;

    /// <summary>
    /// Implement one class per device type.
    /// Read() receives the resolved ConnectionConfig so the driver knows host/port,
    /// plus the DeviceConfig for device-specific parameters (slaveId, bacnetDeviceId, …).
    /// </summary>
    interface IDeviceReader
    {
        string DriverName { get; }

        Telemetry Read(ConnectionConfig connection, DeviceConfig device);
    }

    // =========================================================================
    //  Shared Modbus TCP helpers
    // =========================================================================
    static class ModbusHelper
    {
        public static T WithMaster<T>(ConnectionConfig conn, Func<IModbusMaster, T> action)
        {
            using var tcp    = new TcpClient();
            tcp.Connect(conn.Host, conn.Port);
            using var master = new ModbusFactory().CreateMaster(tcp);
            return action(master);
        }

        /// <summary>Reads two 16-bit holding registers and converts them to a big-endian float32.</summary>
        public static float ReadFloat32(IModbusMaster master, byte slaveId, ushort address)
        {
            var regs = master.ReadHoldingRegisters(slaveId, address, 2);
            return RegsToFloat(regs, 0);
        }

        public static float RegsToFloat(ushort[] regs, int offset)
        {
            var b = new byte[]
            {
                (byte)(regs[offset]     >> 8), (byte)(regs[offset]     & 0xFF),
                (byte)(regs[offset + 1] >> 8), (byte)(regs[offset + 1] & 0xFF),
            };
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToSingle(b, 0);
        }
    }
}
