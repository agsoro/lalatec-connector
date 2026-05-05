// Config.cs – JSON model, mirrors connector.json
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Connector
{
    record AppConfig(
        [property: JsonPropertyName("thingsboard")]   TbConfig?              ThingsBoard,
        [property: JsonPropertyName("polling")]       PollingConfig?         Polling,
        [property: JsonPropertyName("connections")]   List<ConnectionConfig> Connections,
        [property: JsonPropertyName("devices")]       List<DeviceConfig>     Devices,
        [property: JsonPropertyName("monitoring")]    MonitoringConfig?      Monitoring
    );

    // ── Write-back ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables MQTT-driven write-back for a device. Both fields are opt-in (default false).
    /// </summary>
    record WritebackConfig(
        /// <summary>Subscribe to shared-attribute updates from ThingsBoard and write them to the device.</summary>
        [property: JsonPropertyName("sharedAttributes")] bool SharedAttributes,

        /// <summary>Subscribe to server-side RPC calls (method "setValue") and write the value to the device.</summary>
        [property: JsonPropertyName("rpc")]              bool Rpc
    );

    /// <summary>
    /// Maps a single ThingsBoard key to a Modbus holding register (or coil) for write-back.
    /// </summary>
    record ModbusWritableRegister(
        /// <summary>ThingsBoard attribute/telemetry key, e.g. "active_allowed_power_pct".</summary>
        [property: JsonPropertyName("key")]     string Key,

        /// <summary>
        /// 0-based register address. Accepts decimal ("4656") or hex ("0x1230") notation.
        /// For coil type, this is the 0-based coil address.
        /// </summary>
        [property: JsonPropertyName("address")] string Address,

        /// <summary>
        /// Data type for the write:
        ///   "float32-be"  → 2 × 16-bit holding registers, IEEE 754, big-endian word order
        ///   "int32-be"    → 2 × 16-bit holding registers, signed 32-bit, big-endian word order
        ///   "uint32-be"   → 2 × 16-bit holding registers, unsigned 32-bit, big-endian word order
        ///   "int16"       → 1 × 16-bit holding register, signed
        ///   "uint16"      → 1 × 16-bit holding register, unsigned
        ///   "coil"        → single coil (WriteSingleCoil); value != 0 → true
        /// Defaults to "float32-be".
        /// </summary>
        [property: JsonPropertyName("type")]    string Type = "float32-be"
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
        [property: JsonPropertyName("bacnet")]         BacnetDeviceConfig? Bacnet,

        // Write-back (optional – both drivers)
        [property: JsonPropertyName("writeback")]          WritebackConfig?              Writeback,
        [property: JsonPropertyName("writableRegisters")]  List<ModbusWritableRegister>? WritableRegisters,

        /// <summary>
        /// Per-device poll interval in seconds. Overrides the global polling.intervalSeconds.
        /// Controls how often non-COV / fallback-polled values are read from the device.
        /// When omitted, the global default is used.
        /// </summary>
        [property: JsonPropertyName("pollIntervalSeconds")] int? PollIntervalSeconds = null
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
        [property: JsonPropertyName("whoIsTimeoutMs")] int                    WhoIsTimeoutMs,
        [property: JsonPropertyName("filter")]         BacnetFilterConfig     Filter,
        [property: JsonPropertyName("properties")]     BacnetPropsConfig      Properties,
        [property: JsonPropertyName("discovery")]      BacnetDiscoveryConfig  Discovery,

        /// <summary>
        /// Optional Deziko / Structured-View hierarchy extraction.
        /// Enabled only when this block is present and <c>enabled</c> is true.
        /// </summary>
        [property: JsonPropertyName("hierarchy")]      BacnetHierarchyConfig? Hierarchy = null,

        /// <summary>
        /// Optional BACnet Change-of-Value subscription config.
        /// When enabled the connector subscribes to COV notifications from the device
        /// instead of polling PROP_PRESENT_VALUE on every cycle.
        /// </summary>
        [property: JsonPropertyName("cov")]            BacnetCovConfig?       Cov = null
    );

    // ── BACnet COV subscription config ────────────────────────────────────────

    /// <summary>
    /// Configures BACnet Change-of-Value subscriptions for a device.
    /// When <see cref="Enabled"/> is true the connector maintains a long-lived
    /// BACnet client and receives push notifications instead of polling telemetry.
    /// </summary>
    record BacnetCovConfig(
        /// <summary>Activate COV for this device. False = unchanged polling behaviour.</summary>
        [property: JsonPropertyName("enabled")]                   bool  Enabled,

        /// <summary>Subscription lifetime sent to the device in seconds (default 300).</summary>
        [property: JsonPropertyName("lifetimeSeconds")]           uint  LifetimeSeconds = 300,

        /// <summary>Minimum value change to trigger a notification (0 = any change).</summary>
        [property: JsonPropertyName("covIncrement")]              float CovIncrement = 0f,

        /// <summary>True = device sends ConfirmedCOVNotification (requires ACK); false = unconfirmed.</summary>
        [property: JsonPropertyName("confirmedNotifications")]    bool  ConfirmedNotifications = false,

        /// <summary>
        /// How many attribute property reads to perform per minute across all objects.
        /// Attributes (PROP_UNITS, PROP_DESCRIPTION, …) are drip-polled continuously
        /// in a round-robin at this rate. Default: 5 reads/min.
        /// </summary>
        [property: JsonPropertyName("attributePollRatePerMinute")] int  AttributePollRatePerMinute = 5
    );

    // ── Deziko hierarchy config ────────────────────────────────────────────────

    /// <summary>
    /// Controls whether the connector walks OBJECT_STRUCTURED_VIEW objects on the
    /// BACnet device and materialises them as ThingsBoard Assets with "Contains" relations.
    /// </summary>
    record BacnetHierarchyConfig(
        /// <summary>Set to true to enable Structured View extraction for this device.</summary>
        [property: JsonPropertyName("enabled")]   bool   Enabled,

        /// <summary>
        /// ThingsBoard asset type string used when creating asset entities.
        /// Defaults to "BACnet Node" when omitted.
        /// </summary>
        [property: JsonPropertyName("assetType")] string AssetType = "BACnet Node"
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

    // ── Monitoring Dashboard ──────────────────────────────────────────────────

    /// <summary>
    /// Configuration for the embedded read-only monitoring dashboard.
    /// When enabled, a Kestrel HTTP server serves a dashboard on the configured port.
    /// </summary>
    record MonitoringConfig(
        [property: JsonPropertyName("enabled")]     bool   Enabled,
        [property: JsonPropertyName("port")]        int    Port = 5000,
        [property: JsonPropertyName("logBufferSize")] int LogBufferSize = 5000
    );
}
