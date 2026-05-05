// IDeviceReader.cs – common read interface + Modbus TCP helper
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using NModbus;

namespace Connector
{
    using Telemetry = Dictionary<string, object>;

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

    /// <summary>
    /// Optional write-back interface. Implement alongside IDeviceReader on drivers that
    /// support writing values back to the field device.
    /// </summary>
    interface IDeviceWriter
    {
        /// <summary>
        /// Writes one value to the field device.
        /// <paramref name="key"/> is the ThingsBoard telemetry/attribute key
        /// (e.g. "ao_3_supply_sp_value").
        /// Throws on error; the caller logs and acknowledges to ThingsBoard.
        /// </summary>
        void Write(ConnectionConfig connection, DeviceConfig device,
                   string key, double value);
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

        public static int RegsToInt32(ushort[] regs, int offset)
        {
            var b = new byte[]
            {
                (byte)(regs[offset]     >> 8), (byte)(regs[offset]     & 0xFF),
                (byte)(regs[offset + 1] >> 8), (byte)(regs[offset + 1] & 0xFF),
            };
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToInt32(b, 0);
        }

        public static ulong RegsToUInt64(ushort[] regs, int offset)
        {
            var b = new byte[]
            {
                (byte)(regs[offset]     >> 8), (byte)(regs[offset]     & 0xFF),
                (byte)(regs[offset + 1] >> 8), (byte)(regs[offset + 1] & 0xFF),
                (byte)(regs[offset + 2] >> 8), (byte)(regs[offset + 2] & 0xFF),
                (byte)(regs[offset + 3] >> 8), (byte)(regs[offset + 3] & 0xFF),
            };
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToUInt64(b, 0);
        }

        /// <summary>Writes a float32 value to two contiguous holding registers (big-endian).</summary>
        public static void WriteFloat32(IModbusMaster master, byte slaveId, ushort address, float value)
        {
            var b = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            // big-endian register order: b[0..1] first register, b[2..3] second register
            var regs = new ushort[]
            {
                (ushort)((b[0] << 8) | b[1]),
                (ushort)((b[2] << 8) | b[3]),
            };
            master.WriteMultipleRegisters(slaveId, address, regs);
        }
    }
}
