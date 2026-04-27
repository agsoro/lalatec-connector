// DeviceReaderFactory.cs – maps "deviceType" string from connector.json → driver instance
using System;
using System.Collections.Generic;

namespace Connector
{
    static class DeviceReaderFactory
    {
        // ── Register new device types here ─────────────────────────────────────
        static readonly Dictionary<string, IDeviceReader> Readers =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["janitza"]        = new JanitzaReader(),
                ["glueck"]         = new GlueckReader(),
                ["bacnet-generic"] = new BacnetReader(),
            };

        public static IDeviceReader Get(string deviceType)
        {
            if (Readers.TryGetValue(deviceType, out var reader))
                return reader;

            throw new NotSupportedException(
                $"Unknown deviceType '{deviceType}'. " +
                $"Known types: {string.Join(", ", Readers.Keys)}");
        }

        /// <summary>
        /// Returns the <see cref="IDeviceWriter"/> for the given device type,
        /// or <c>null</c> if the driver does not support write-back.
        /// </summary>
        public static IDeviceWriter? GetWriter(string deviceType)
        {
            if (Readers.TryGetValue(deviceType, out var reader) && reader is IDeviceWriter w)
                return w;
            return null;
        }
    }
}
