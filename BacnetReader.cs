// BacnetReader.cs – BACnet/IP device reader with full object discovery and filtering
//
//  Discovery flow (run on startup and optionally on a timer):
//    1. Unicast Who-Is  →  I-Am  →  resolves BacnetAddress
//    2. Read DEVICE:bacnetDeviceId / PROP_OBJECT_LIST  →  full object list
//       (falls back to segmented index-by-index read if the device doesn't
//        support returning the whole list at once)
//    3. For every object, read PROP_OBJECT_NAME to get the human-readable name
//    4. Apply the filter block from config (objectTypes, instanceRange, namePattern,
//       excludeNamePattern)
//    5. Cache the surviving list.  On every poll, call ReadPropertyMultiple on
//       the cached objects to read telemetry + attribute properties.
//
//  ThingsBoard distinction:
//    telemetry  properties  → published as time-series (ts + values)
//    attributes properties  → published to /v1/devices/me/attributes (key-value, no timestamp)
//
//  NuGet:  BACnet 3.0.2  (ela-compil / System.IO.BACnet)

using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Connector
{
    using Telemetry  = Dictionary<string, double>;
    using Attributes = Dictionary<string, string>;

    // =========================================================================
    //  Result returned by BacnetReader – extends the base Telemetry with
    //  an optional Attributes bag for ThingsBoard device attributes.
    // =========================================================================
    class BacnetReadResult
    {
        public Telemetry  Telemetry  { get; } = new();
        public Attributes Attributes { get; } = new();
    }

    // =========================================================================
    //  Discovered object descriptor (cached after discovery)
    // =========================================================================
    record BacnetObjectInfo(
        BacnetObjectId ObjectId,
        string         ObjectName,           // from PROP_OBJECT_NAME
        string         Description = "",     // from PROP_DESCRIPTION (when filter active)
        bool           Commandable = false   // true when PROP_PRIORITY_ARRAY is present
    )
    {
        /// <summary>Sanitised key prefix used in ThingsBoard, e.g. "ai_0_room_temp".</summary>
        public string KeyPrefix =>
            $"{ShortTypeName(ObjectId.type)}_{ObjectId.instance}_" +
            Sanitise(ObjectName);

        static string ShortTypeName(BacnetObjectTypes t) => t switch
        {
            BacnetObjectTypes.OBJECT_ANALOG_INPUT        => "ai",
            BacnetObjectTypes.OBJECT_ANALOG_OUTPUT       => "ao",
            BacnetObjectTypes.OBJECT_ANALOG_VALUE        => "av",
            BacnetObjectTypes.OBJECT_BINARY_INPUT        => "bi",
            BacnetObjectTypes.OBJECT_BINARY_OUTPUT       => "bo",
            BacnetObjectTypes.OBJECT_BINARY_VALUE        => "bv",
            BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT   => "mi",
            BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT  => "mo",
            BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE   => "mv",
            BacnetObjectTypes.OBJECT_DEVICE              => "dev",
            _                                            => t.ToString().Replace("OBJECT_", "").ToLower(),
        };

        static string Sanitise(string name) =>
            Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "_").Trim('_');
    }

    // =========================================================================
    //  The reader + writer
    // =========================================================================
    class BacnetReader : IDeviceReader, IDeviceWriter
    {
        public string DriverName => "BACnet/IP";

        // Per-device discovery state (keyed by device name so multiple devices work)
        readonly Dictionary<string, DiscoveryState> _stateByDevice = new();
        readonly object                              _stateLock     = new();

        // =====================================================================
        //  IDeviceReader.Read  –  called by the polling loop
        // =====================================================================
        public Telemetry Read(ConnectionConfig conn, DeviceConfig device)
        {
            // BacnetReader.Read() only returns telemetry for the base interface.
            // Call ReadFull() from Program.cs to get both telemetry and attributes.
            return ReadFull(conn, device).Telemetry;
        }

        /// <summary>Returns both telemetry and attributes. Called directly from Program.cs.</summary>
        public BacnetReadResult ReadFull(ConnectionConfig conn, DeviceConfig device)
        {
            if (device.BacnetDeviceId is null)
                throw new InvalidOperationException($"Device '{device.Name}' is missing bacnetDeviceId.");

            var cfg = device.Bacnet
                ?? throw new InvalidOperationException($"Device '{device.Name}' is missing the 'bacnet' config block.");

            using var client = OpenClient(conn);

            // ── Resolve BacnetAddress ─────────────────────────────────────────
            var address = ResolveAddress(client, conn.Host, conn.Port,
                                         device.BacnetDeviceId.Value,
                                         cfg.WhoIsTimeoutMs > 0 ? cfg.WhoIsTimeoutMs : 2000);
            // ResolveAddress always returns a non-null address (direct IP fallback)

            // ── Discovery (lazy + periodic) ───────────────────────────────────
            var state = GetOrCreateState(device.Name);
            bool needsDiscovery = cfg.Discovery.OnStartup && !state.DiscoveryDone
                               || (cfg.Discovery.RefreshIntervalMinutes > 0
                                   && DateTime.UtcNow - state.LastDiscovery
                                      > TimeSpan.FromMinutes(cfg.Discovery.RefreshIntervalMinutes));

            if (needsDiscovery)
            {
                Console.WriteLine($"  [BACnet] Discovering objects on {device.Name}…");
                var all      = DiscoverObjects(client, address, device.BacnetDeviceId.Value, cfg);
                var filtered = ApplyFilter(client, address, all, cfg.Filter);
                state.CachedObjects    = filtered;
                state.LastDiscovery    = DateTime.UtcNow;
                state.DiscoveryDone    = true;
                state.AttributesSent   = false;   // re-send attributes after rediscovery
                Console.WriteLine($"  [BACnet] {device.Name}: {all.Count} objects found, " +
                                  $"{filtered.Count} after filter.");
            }

            if (state.CachedObjects.Count == 0)
                return new BacnetReadResult();

            // ── Read properties ───────────────────────────────────────────────
            var result = new BacnetReadResult();

            var telPropIds  = ParsePropertyIds(cfg.Properties.Telemetry);
            var attrPropIds = !state.AttributesSent
                              ? ParsePropertyIds(cfg.Properties.Attributes)
                              : Array.Empty<BacnetPropertyIds>();

            var allPropIds = telPropIds.Concat(attrPropIds).Distinct().ToArray();

            foreach (var obj in state.CachedObjects)
            {
                var values = ReadObjectProperties(client, address, obj.ObjectId, allPropIds);

                // Telemetry
                foreach (var propId in telPropIds)
                {
                    if (values.TryGetValue(propId, out var raw) && TryToDouble(raw, out double d))
                    {
                        string key = $"{obj.KeyPrefix}_{PropSuffix(propId)}";
                        result.Telemetry[key] = Math.Round(d, 4);
                    }
                }

                // Attributes (only on first poll after discovery)
                if (!state.AttributesSent)
                {
                    foreach (var propId in attrPropIds)
                    {
                        if (values.TryGetValue(propId, out var raw))
                        {
                            string key = $"{obj.KeyPrefix}_{PropSuffix(propId)}";
                            result.Attributes[key] = raw?.ToString() ?? "";
                        }
                    }
                }
            }

            if (!state.AttributesSent && attrPropIds.Length > 0)
                state.AttributesSent = true;

            return result;
        }

        // =====================================================================
        //  Discovery – reads PROP_OBJECT_LIST from the DEVICE object
        // =====================================================================
        static List<BacnetObjectId> DiscoverObjects(
            BacnetClient client, BacnetAddress address, uint deviceId,
            BacnetDeviceConfig cfg)
        {
            var deviceObjId = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, deviceId);
            var objectIds   = new List<BacnetObjectId>();

            // Try reading the whole list at once first
            if (client.ReadPropertyRequest(address, deviceObjId,
                    BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> listValues))
            {
                foreach (var v in listValues)
                    if (v.Value is BacnetObjectId oid)
                        objectIds.Add(oid);

                return objectIds;
            }

            // Fallback: some devices segment PROP_OBJECT_LIST – read index 0 for count
            // then read each entry by index (array index 1-based per BACnet spec)
            Console.WriteLine("  [BACnet] PROP_OBJECT_LIST bulk read failed – using index fallback…");

            if (!client.ReadPropertyRequest(address, deviceObjId,
                    BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> countVal,
                    arrayIndex: 0))
            {
                Console.Error.WriteLine("  [BACnet] Could not read object list length.");
                return objectIds;
            }

            uint count = Convert.ToUInt32(countVal[0].Value);
            Console.WriteLine($"  [BACnet] Reading {count} objects by index…");

            for (uint i = 1; i <= count; i++)
            {
                if (client.ReadPropertyRequest(address, deviceObjId,
                        BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> entry,
                        arrayIndex: i)
                    && entry.Count > 0
                    && entry[0].Value is BacnetObjectId eid)
                {
                    objectIds.Add(eid);
                }
            }

            return objectIds;
        }

        // =====================================================================
        //  Object-type alias table  (short name / numeric string → enum)
        // =====================================================================
        static readonly Dictionary<string, BacnetObjectTypes> _typeAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["AI"]  = BacnetObjectTypes.OBJECT_ANALOG_INPUT,
                ["AO"]  = BacnetObjectTypes.OBJECT_ANALOG_OUTPUT,
                ["AV"]  = BacnetObjectTypes.OBJECT_ANALOG_VALUE,
                ["BI"]  = BacnetObjectTypes.OBJECT_BINARY_INPUT,
                ["BO"]  = BacnetObjectTypes.OBJECT_BINARY_OUTPUT,
                ["BV"]  = BacnetObjectTypes.OBJECT_BINARY_VALUE,
                ["MI"]  = BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT,
                ["MO"]  = BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT,
                ["MV"]  = BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE,
            };

        /// <summary>
        /// Resolves one entry from the objectTypes allowlist.
        /// Accepts (in order):
        ///   1. Short alias     e.g. "AI", "BO"
        ///   2. Full enum name  e.g. "OBJECT_ANALOG_INPUT"
        ///   3. Numeric type ID e.g. "0", "128"
        /// Returns null and logs a warning when the entry cannot be resolved.
        /// </summary>
        static BacnetObjectTypes? ResolveObjectType(string entry)
        {
            // 1. Short alias
            if (_typeAliases.TryGetValue(entry, out var aliased))
                return aliased;

            // 2. Full enum name (case-insensitive)
            if (Enum.TryParse<BacnetObjectTypes>(entry, ignoreCase: true, out var named))
                return named;

            // 3. Numeric ID
            if (uint.TryParse(entry, out uint numericId))
                return (BacnetObjectTypes)numericId;

            Console.Error.WriteLine($"  [BACnet] Cannot resolve objectType filter entry: '{entry}'");
            return null;
        }

        // =====================================================================
        //  Filter – reads PROP_OBJECT_NAME / PROP_DESCRIPTION, applies rules,
        //           probes PROP_PRIORITY_ARRAY to detect commandable objects
        // =====================================================================
        static List<BacnetObjectInfo> ApplyFilter(
            BacnetClient client, BacnetAddress address,
            List<BacnetObjectId> candidates, BacnetFilterConfig filter)
        {
            // Build type allowlist (null = accept all)
            HashSet<BacnetObjectTypes>? allowedTypes = null;
            if (filter.ObjectTypes is { Count: > 0 })
            {
                allowedTypes = new();
                foreach (var entry in filter.ObjectTypes)
                {
                    var t = ResolveObjectType(entry);
                    if (t.HasValue) allowedTypes.Add(t.Value);
                }
            }

            // Pre-compile regex objects (null = not active)
            Regex? includeNameRx = filter.NamePattern        is not null ? new Regex(filter.NamePattern,        RegexOptions.IgnoreCase) : null;
            Regex? excludeNameRx = filter.ExcludeNamePattern is not null ? new Regex(filter.ExcludeNamePattern, RegexOptions.IgnoreCase) : null;
            Regex? includeDescRx = filter.DescriptionPattern is not null ? new Regex(filter.DescriptionPattern, RegexOptions.IgnoreCase) : null;

            bool needName = includeNameRx != null || excludeNameRx != null;
            bool needDesc = includeDescRx != null;

            int cap = (filter.MaxObjects is > 0) ? filter.MaxObjects.Value : int.MaxValue;

            var result = new List<BacnetObjectInfo>();

            foreach (var oid in candidates)
            {
                if (result.Count >= cap) break;

                // Skip DEVICE object itself – not a data point
                if (oid.type == BacnetObjectTypes.OBJECT_DEVICE) continue;

                // 1. Type allowlist (no network I/O)
                if (allowedTypes != null && !allowedTypes.Contains(oid.type)) continue;

                // 2. Instance range (no network I/O)
                if (filter.InstanceRange is not null)
                    if (oid.instance < filter.InstanceRange.Min ||
                        oid.instance > filter.InstanceRange.Max) continue;

                // 3. Name filters – read PROP_OBJECT_NAME only when needed
                string objectName = needName || needDesc
                    ? ReadObjectName(client, address, oid)
                    : oid.ToString();

                if (includeNameRx != null && !includeNameRx.IsMatch(objectName)) continue;
                if (excludeNameRx != null &&  excludeNameRx.IsMatch(objectName)) continue;

                // 4. Description filter – read PROP_DESCRIPTION only when needed
                string description = "";
                if (needDesc)
                {
                    description = ReadDescription(client, address, oid);
                    if (!includeDescRx!.IsMatch(description)) continue;
                }

                // 5. Commandability probe:
                //    BACnet-standard: only objects with PROP_PRIORITY_ARRAY are commandable.
                //    We only probe output/value types (AO, AV, BO, BV, MO, MV) to
                //    avoid unnecessary network I/O on sensor inputs.
                bool commandable = false;
                if (IsCommandableType(oid.type))
                {
                    try
                    {
                        commandable = client.ReadPropertyRequest(
                            address, oid,
                            BacnetPropertyIds.PROP_PRIORITY_ARRAY,
                            out IList<BacnetValue> _);
                    }
                    catch { /* device may reject – treat as non-commandable */ }
                }

                result.Add(new BacnetObjectInfo(oid, objectName, description, commandable));
            }

            return result;
        }

        /// <summary>
        /// Returns true for BACnet object types that always have a priority array
        /// and are therefore operator-writable (commandable). Matches the Siemens
        /// Desigo convention: AO, AV, BO, BV, MO, MV are setpoints/outputs;
        /// AI, BI, MI are sensor inputs and are never commandable.
        /// </summary>
        static bool IsCommandableType(BacnetObjectTypes t) => t is
            BacnetObjectTypes.OBJECT_ANALOG_OUTPUT      or
            BacnetObjectTypes.OBJECT_ANALOG_VALUE       or
            BacnetObjectTypes.OBJECT_BINARY_OUTPUT      or
            BacnetObjectTypes.OBJECT_BINARY_VALUE       or
            BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT or
            BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE;


        static string ReadObjectName(BacnetClient client, BacnetAddress address, BacnetObjectId oid)
        {
            try
            {
                if (client.ReadPropertyRequest(address, oid,
                        BacnetPropertyIds.PROP_OBJECT_NAME, out IList<BacnetValue> vals)
                    && vals.Count > 0)
                    return vals[0].Value?.ToString() ?? oid.ToString();
            }
            catch { /* fall through */ }
            return oid.ToString();
        }

        static string ReadDescription(BacnetClient client, BacnetAddress address, BacnetObjectId oid)
        {
            try
            {
                if (client.ReadPropertyRequest(address, oid,
                        BacnetPropertyIds.PROP_DESCRIPTION, out IList<BacnetValue> vals)
                    && vals.Count > 0)
                    return vals[0].Value?.ToString() ?? "";
            }
            catch { /* fall through */ }
            return "";
        }

        // =====================================================================
        //  Read multiple properties from one object
        // =====================================================================
        static Dictionary<BacnetPropertyIds, object?> ReadObjectProperties(
            BacnetClient client, BacnetAddress address,
            BacnetObjectId oid, BacnetPropertyIds[] propIds)
        {
            var result = new Dictionary<BacnetPropertyIds, object?>();

            // Build a ReadPropertyMultiple request for all props at once
            var propRefs = propIds
                .Select(p => new BacnetPropertyReference((uint)p, uint.MaxValue))
                .ToList();

            var readReq = new List<BacnetReadAccessSpecification>
            {
                new BacnetReadAccessSpecification(oid, propRefs)
            };

            if (client.ReadPropertyMultipleRequest(address, readReq, out IList<BacnetReadAccessResult> results))
            {
                foreach (var res in results)
                    foreach (var pv in res.values)
                    {
                        var propId = (BacnetPropertyIds)pv.property.propertyIdentifier;
                        result[propId] = pv.value?.Count > 0 ? pv.value[0].Value : null;
                    }
                return result;
            }

            // Fallback: ReadPropertyMultiple not supported – read one by one
            foreach (var propId in propIds)
            {
                try
                {
                    if (client.ReadPropertyRequest(address, oid, propId, out IList<BacnetValue> vals)
                        && vals.Count > 0)
                        result[propId] = vals[0].Value;
                }
                catch { /* property not available on this object */ }
            }

            return result;
        }

        // =====================================================================
        //  Address resolution
        //
        //  When host+port are already known we can construct the BacnetAddress
        //  directly – no need for a broadcast Who-Is.
        //  We still send a local-broadcast Who-Is so the device's I-Am can
        //  confirm it is alive; if no I-Am arrives within the timeout we use
        //  the address we built from config (reliable for unicast IP devices).
        // =====================================================================
        static BacnetAddress ResolveAddress(
            BacnetClient client, string host, int port, uint deviceId, int timeoutMs)
        {
            // Build the direct address from config (works for unicast BACnet/IP)
            var directAddress = new BacnetAddress(BacnetAddressTypes.IP, $"{host}:{port}");

            BacnetAddress? iamAddress = null;
            using var      signal     = new ManualResetEventSlim(false);

            void OnIam(BacnetClient _, BacnetAddress adr, uint id,
                       uint maxApdu, BacnetSegmentations seg, ushort vendor)
            {
                if (id == deviceId) { iamAddress = adr; signal.Set(); }
            }

            client.OnIam += OnIam;
            try
            {
                // Broadcast Who-Is (no range) – the target device on the same
                // subnet will reply with I-Am so we can confirm it is alive.
                client.WhoIs();
                signal.Wait(timeoutMs);
            }
            finally { client.OnIam -= OnIam; }

            // Prefer the address from I-Am (contains real transport info);
            // fall back to the address constructed from config.
            return iamAddress ?? directAddress;
        }

        // =====================================================================
        //  Helpers
        // =====================================================================

        static BacnetClient OpenClient(ConnectionConfig conn)
        {
            var transport = new BacnetIpUdpProtocolTransport(port: 0, useExclusivePort: true);
            var client    = new BacnetClient(transport);
            client.Start();
            return client;
        }

        DiscoveryState GetOrCreateState(string deviceName)
        {
            lock (_stateLock)
            {
                if (!_stateByDevice.TryGetValue(deviceName, out var s))
                {
                    s = new DiscoveryState();
                    _stateByDevice[deviceName] = s;
                }
                return s;
            }
        }

        static BacnetPropertyIds[] ParsePropertyIds(IEnumerable<string> names) =>
            names
                .Select(n => Enum.TryParse<BacnetPropertyIds>(n, true, out var id) ? (BacnetPropertyIds?)id : null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToArray();

        static string PropSuffix(BacnetPropertyIds p) => p switch
        {
            BacnetPropertyIds.PROP_PRESENT_VALUE  => "value",
            BacnetPropertyIds.PROP_OBJECT_NAME    => "name",
            BacnetPropertyIds.PROP_DESCRIPTION    => "desc",
            BacnetPropertyIds.PROP_UNITS          => "units",
            BacnetPropertyIds.PROP_STATUS_FLAGS   => "status",
            BacnetPropertyIds.PROP_OUT_OF_SERVICE => "oos",
            _                                     => p.ToString().Replace("PROP_", "").ToLower(),
        };

        static bool TryToDouble(object? v, out double result)
        {
            if (v is null) { result = 0; return false; }
            try   { result = Math.Round(Convert.ToDouble(v), 6); return true; }
            catch { result = 0; return false; }
        }

        // ── Per-device mutable discovery state ───────────────────────────────

        class DiscoveryState
        {
            public bool                   DiscoveryDone   { get; set; }
            public bool                   AttributesSent  { get; set; }
            public DateTime               LastDiscovery   { get; set; } = DateTime.MinValue;
            public List<BacnetObjectInfo> CachedObjects   { get; set; } = new();
        }

        // =====================================================================
        //  IDeviceWriter – write a value to a BACnet object's PROP_PRESENT_VALUE
        // =====================================================================
        public void Write(ConnectionConfig conn, DeviceConfig device, string key, double value)
        {
            if (device.BacnetDeviceId is null)
                throw new InvalidOperationException(
                    $"Device '{device.Name}' is missing bacnetDeviceId.");

            var cfg = device.Bacnet
                ?? throw new InvalidOperationException(
                    $"Device '{device.Name}' is missing the 'bacnet' config block.");

            // Resolve target object from the ThingsBoard key
            if (!TryParseKeyToObjectId(key, out BacnetObjectId objectId))
                throw new ArgumentException(
                    $"Cannot parse BACnet object from key '{key}'. " +
                    "Expected format: {type}_{instance}_{name}_{prop}, e.g. ao_3_supply_sp_value.");

            // Check commandability from the cached discovery state
            var state = GetOrCreateState(device.Name);
            var obj   = state.CachedObjects
                            .FirstOrDefault(o => o.ObjectId == objectId);

            if (obj is null)
                throw new KeyNotFoundException(
                    $"Object for key '{key}' ({objectId}) not found in discovery cache for '{device.Name}'. " +
                    "Trigger a rediscovery or check the key name.");

            if (!obj.Commandable)
                throw new InvalidOperationException(
                    $"Object '{key}' ({objectId}) is not commandable (no PROP_PRIORITY_ARRAY). " +
                    "Write rejected to protect the device logic.");

            using var client  = OpenClient(conn);
            var address = ResolveAddress(
                client, conn.Host, conn.Port,
                device.BacnetDeviceId.Value,
                cfg.WhoIsTimeoutMs > 0 ? cfg.WhoIsTimeoutMs : 2000);

            // Choose the right application tag:
            //   Binary objects expect an ENUMERATED (0/1); all others expect REAL
            BacnetValue bv = objectId.type is
                BacnetObjectTypes.OBJECT_BINARY_OUTPUT or
                BacnetObjectTypes.OBJECT_BINARY_VALUE
                ? new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED,
                                  (uint)(value != 0 ? 1 : 0))
                : new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,
                                  (float)value);

            bool ok = client.WritePropertyRequest(
                address, objectId,
                BacnetPropertyIds.PROP_PRESENT_VALUE,
                new List<BacnetValue> { bv });

            if (!ok)
                throw new Exception(
                    $"BACnet WriteProperty returned NAK for key '{key}' ({objectId}).");
        }

        /// <summary>
        /// Parses a ThingsBoard key such as "ao_3_supply_temp_sp_value" into its
        /// BacnetObjectId by extracting the short type alias and instance number
        /// from the first two underscore-delimited tokens.
        /// </summary>
        static bool TryParseKeyToObjectId(string key, out BacnetObjectId result)
        {
            result = default;

            // Split on '_'; first token = type alias, second = instance number
            var parts = key.Split('_');
            if (parts.Length < 2) return false;

            if (!uint.TryParse(parts[1], out uint instance)) return false;

            if (!_typeAliases.TryGetValue(parts[0], out BacnetObjectTypes objType))
                return false;   // only well-known short aliases are accepted for writes

            result = new BacnetObjectId(objType, instance);
            return true;
        }
    }
}
