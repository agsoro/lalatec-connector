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
    }
}
