// Config.cs – JSON model, mirrors connector.json
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Connector
{
    record AppConfig(
        [property: JsonPropertyName("thingsboard")] TbConfig               ThingsBoard,
        [property: JsonPropertyName("polling")]     PollingConfig          Polling,
        [property: JsonPropertyName("connections")] List<ConnectionConfig> Connections,
        [property: JsonPropertyName("devices")]     List<DeviceConfig>     Devices
    );

    // ── ThingsBoard ────────────────────────────────────────────────────────────

    record TbConfig(
        [property: JsonPropertyName("host")]          string Host,
        [property: JsonPropertyName("httpPort")]      int    HttpPort,
        [property: JsonPropertyName("mqttPort")]      int    MqttPort,
        [property: JsonPropertyName("adminEmail")]    string AdminEmail,
        [property: JsonPropertyName("adminPassword")] string AdminPassword
    );

    record PollingConfig(
        [property: JsonPropertyName("intervalSeconds")] int IntervalSeconds
    );

    // ── Connections ────────────────────────────────────────────────────────────

    record ConnectionConfig(
        [property: JsonPropertyName("id")]   string Id,
        [property: JsonPropertyName("type")] string Type,   // "modbus-tcp" | "bacnet-ip"
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("port")] int    Port
    );

    // ── Devices ───────────────────────────────────────────────────────────────
    //
    //  deviceType:
    //    "janitza"  → JanitzaReader  (needs slaveId)
    //    "glueck"   → GlueckReader   (needs slaveId)
    //    "bacnet"   → BacnetReader   (needs bacnetDeviceId + bacnet config block)

    record DeviceConfig(
        [property: JsonPropertyName("name")]          string             Name,
        [property: JsonPropertyName("deviceType")]    string             DeviceType,
        [property: JsonPropertyName("connectionId")]  string             ConnectionId,

        // Modbus
        [property: JsonPropertyName("slaveId")]       byte?              SlaveId,

        // BACnet
        [property: JsonPropertyName("bacnetDeviceId")] uint?             BacnetDeviceId,
        [property: JsonPropertyName("bacnet")]         BacnetDeviceConfig? Bacnet
    )
    {
        public string AccessToken =>
            "device_" + Name.ToLowerInvariant()
                            .Replace(' ', '_')
                            .Replace('/', '_')
                            .Replace('\\', '_');
    }

    // ── BACnet-specific device config ──────────────────────────────────────────

    record BacnetDeviceConfig(
        [property: JsonPropertyName("whoIsTimeoutMs")] int                WhoIsTimeoutMs,
        [property: JsonPropertyName("filter")]         BacnetFilterConfig Filter,
        [property: JsonPropertyName("properties")]     BacnetPropsConfig  Properties,
        [property: JsonPropertyName("discovery")]      BacnetDiscoveryConfig Discovery
    );

    /// <summary>
    /// Controls which BACnet objects are included after discovery.
    /// All fields are optional – omitting a field means "no restriction on that dimension".
    /// </summary>
    record BacnetFilterConfig(
        /// <summary>
        /// Allowlist of BACnet object types to include. Each entry may be:
        ///   • Full enum name  – "OBJECT_ANALOG_INPUT"
        ///   • Short alias     – "AI", "AO", "AV", "BI", "BO", "BV", "MI", "MO", "MV"
        ///   • Numeric type ID – "0" … "1023"  (covers proprietary/vendor types ≥ 128)
        /// If null or empty → all object types are included.
        /// </summary>
        [property: JsonPropertyName("objectTypes")]           List<string>?  ObjectTypes,

        /// <summary>Inclusive instance number range filter. Null = no limit.</summary>
        [property: JsonPropertyName("instanceRange")]         InstanceRange? InstanceRange,

        /// <summary>Regex applied to PROP_OBJECT_NAME (include). Null = match all.</summary>
        [property: JsonPropertyName("namePattern")]           string?        NamePattern,

        /// <summary>
        /// Regex applied to PROP_OBJECT_NAME. Objects whose name matches are excluded.
        /// Applied after namePattern. Null = nothing excluded.
        /// </summary>
        [property: JsonPropertyName("excludeNamePattern")]    string?        ExcludeNamePattern,

        /// <summary>
        /// Regex applied to PROP_DESCRIPTION (include). Only objects whose description
        /// matches are kept. PROP_DESCRIPTION is only read when this filter is active.
        /// Null = no description filter.
        /// </summary>
        [property: JsonPropertyName("descriptionPattern")]    string?        DescriptionPattern,

        /// <summary>
        /// Maximum number of objects to keep after all other filters. 0 or null = unlimited.
        /// Useful as a safety cap during initial integration / testing.
        /// </summary>
        [property: JsonPropertyName("maxObjects")]            int?           MaxObjects
    );

    record InstanceRange(
        [property: JsonPropertyName("min")] uint Min,
        [property: JsonPropertyName("max")] uint Max
    );

    /// <summary>
    /// Which BACnet properties to read on every poll cycle and how to treat them in ThingsBoard.
    /// Property names use BacnetPropertyIds enum names, e.g. "PROP_PRESENT_VALUE".
    /// </summary>
    record BacnetPropsConfig(
        /// <summary>Published as ThingsBoard timeseries on every poll.</summary>
        [property: JsonPropertyName("telemetry")]   List<string> Telemetry,

        /// <summary>Published as ThingsBoard attributes once on startup (and after rediscovery).</summary>
        [property: JsonPropertyName("attributes")]  List<string> Attributes
    );

    /// <summary>Controls how and when the full object list is (re-)fetched from the device.</summary>
    record BacnetDiscoveryConfig(
        /// <summary>Run object discovery immediately on first poll.</summary>
        [property: JsonPropertyName("onStartup")]             bool OnStartup,

        /// <summary>Re-discover after this many minutes (0 = never refresh).</summary>
        [property: JsonPropertyName("refreshIntervalMinutes")] int  RefreshIntervalMinutes
    );
}
