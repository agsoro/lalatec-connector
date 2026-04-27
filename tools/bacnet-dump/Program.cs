// bacnet-dump – CLI tool to dump all objects and their properties from a BACnet/IP device.
//
// Usage:
//   bacnet-dump <host[:port]> [<device-id>] [--filter <wildcard>] [--timeout <ms>]
//                             [--port <local-port>] [--json] [--all-props]
//
// Examples:
//   bacnet-dump 192.168.1.10                       # discover device id, dump all objects
//   bacnet-dump 192.168.1.10:47808 1001            # target specific device id
//   bacnet-dump 192.168.1.10 1001 --filter "AI*"   # only objects whose name starts with AI
//   bacnet-dump 192.168.1.10 1001 --filter "*Temp*" --json
//   bacnet-dump localhost 1001 --filter "*" --all-props
//
// Wildcard supports:
//   *   – any sequence of characters
//   ?   – any single character
//   The filter is matched case-insensitively against PROP_OBJECT_NAME.
//   Omitting --filter (or passing "*") dumps everything.
//
// NuGet: BACnet 3.0.2 (ela-compil / System.IO.BACnet)

using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;



// ─── Deziko / BACnet extension property IDs ─────────────────────────────────
// These are standard ASHRAE 135 property IDs that Deziko actively uses
// for its engineering hierarchy.  They are not present in the ela-compil enum
// by name, so we cast from the numeric value (matches BACnet spec clause 12).
const BacnetPropertyIds PROP_STRUCTURED_OBJECT_LIST = (BacnetPropertyIds)209; // on DEVICE
const BacnetPropertyIds PROP_SUBORDINATE_LIST        = (BacnetPropertyIds)355; // on STRUCTURED_VIEW

// ─── Parse arguments ─────────────────────────────────────────────────────────

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

string  rawHost    = args[0];
uint?   deviceId   = null;
string  filter     = "*";
int     whoIsMs    = 3000;
int     localPort  = 0;       // 0 = OS-assigned ephemeral port
bool    jsonOutput    = false;
bool    simJsonOutput = false;
bool    allProps      = false;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--filter" when i + 1 < args.Length:  filter    = args[++i]; break;
        case "--timeout" when i + 1 < args.Length: whoIsMs   = int.Parse(args[++i]); break;
        case "--port" when i + 1 < args.Length:    localPort = int.Parse(args[++i]); break;

        case "--filter":
        case "--timeout":
        case "--port":
            Console.Error.WriteLine($"Option '{args[i]}' requires a value.");
            Console.Error.WriteLine();
            PrintUsage();
            return 2;

        case "--json":      jsonOutput    = true; break;
        case "--sim-json":  simJsonOutput = true; break;
        case "--all-props": allProps      = true; break;

        default:
            if (uint.TryParse(args[i], out uint id))
                deviceId = id;
            else
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                Console.Error.WriteLine();
                PrintUsage();
                return 2;
            }
            break;
    }
}

// Parse host[:port]
string host    = rawHost;
int    remPort = 47808;
if (rawHost.Contains(':'))
{
    var parts = rawHost.Split(':', 2);
    host    = parts[0];
    remPort = int.Parse(parts[1]);
}

// ─── Resolve hostname ─────────────────────────────────────────────────────────
string ip = ResolveHost(host);

// ─── Open BACnet client ───────────────────────────────────────────────────────
var transport = new BacnetIpUdpProtocolTransport(localPort, useExclusivePort: true);
var client    = new BacnetClient(transport);
client.Start();

Info($"BACnet/IP dump  target={ip}:{remPort}  filter=\"{filter}\"");

// ─── Device discovery (Who-Is / I-Am) ────────────────────────────────────────
//
//  Two strategies fired in parallel, sharing one signal:
//
//    1. Broadcast Who-Is  – works when device is on the same IP subnet
//    2. Unicast Who-Is    – works across routers / BBMD; requires the target IP
//
//  Whichever triggers an I-Am first wins.  If neither succeeds within the
//  timeout we fall back to the direct address we already constructed (silent
//  connect – works for many real devices that skip the Who-Is handshake).

// Validate / build the direct unicast address before starting discovery so we
// can pass it as the unicast Who-Is target too.
if (!System.Net.IPAddress.TryParse(ip, out _))
{
    Error($"'{host}' could not be resolved to a valid IP address.");
    Error("Try: bacnet-dump --help");
    client.Dispose();
    return 1;
}
var directAddress = new BacnetAddress(BacnetAddressTypes.IP, $"{ip}:{remPort}");

BacnetAddress? address = null;
uint           foundId = 0;

using (var signal = new ManualResetEventSlim(false))
{
    void OnIam(BacnetClient _, BacnetAddress adr, uint id,
               uint maxApdu, BacnetSegmentations seg, ushort vendor)
    {
        if (deviceId.HasValue && id != deviceId.Value) return;
        if (address != null) return;   // take first match
        address = adr;
        foundId = id;
        signal.Set();
    }

    client.OnIam += OnIam;

    // 1. Broadcast (same subnet)
    client.WhoIs(-1, -1, null, null);

    // 2. Unicast (across routers / when broadcast is blocked)
    //    lowLimit/highLimit of -1 means "any device" in the ela-compil library.
    client.WhoIs(-1, -1, directAddress, null);

    signal.Wait(whoIsMs);
    client.OnIam -= OnIam;
}

if (address == null)
{
    // No I-Am received.  Use the direct address we built above.
    // Many real devices skip Who-Is/I-Am entirely and just answer
    // ReadProperty straight away, so this is a normal code path.
    address = directAddress;
    foundId = deviceId ?? 0;
    Warn("No I-Am received – using direct-address fallback.");
    if (foundId == 0)
    {
        Error("No device replied to Who-Is and no <device-id> argument given.");
        Error("Supply the device instance number as the second positional argument,");
        Error("or increase --timeout if the device is slow to respond.");
        Error("Try: bacnet-dump --help");
        client.Dispose();
        return 1;
    }
}


Info($"Device found: id={foundId}  address={address}");

// ─── Read PROP_OBJECT_LIST from DEVICE object ─────────────────────────────────
var deviceObjId = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, foundId);
var allObjects  = ReadObjectList(client, address, deviceObjId);

if (allObjects.Count == 0)
{
    Error("Could not read PROP_OBJECT_LIST from the device.");
    Error("Check that the device is reachable and the device-id is correct.");
    Error("Try: bacnet-dump --help");
    client.Dispose();
    return 1;
}

Info($"Object list: {allObjects.Count} object(s) in PROP_OBJECT_LIST");

// ─── Resolve PROP_OBJECT_NAME for every object, apply wildcard filter ─────────
var regex = WildcardToRegex(filter);

var objects = new List<(BacnetObjectId Id, string Name, string Description)>();
foreach (var oid in allObjects)
{
    string name = ReadStringProp(client, address, oid, BacnetPropertyIds.PROP_OBJECT_NAME)
                  ?? oid.ToString();
    if (!regex.IsMatch(name) && !regex.IsMatch(oid.ToString())) continue;

    string desc = ReadStringProp(client, address, oid, BacnetPropertyIds.PROP_DESCRIPTION) ?? "";
    objects.Add((oid, name, desc));
}

Info($"Matched {objects.Count} object(s) after applying filter \"{filter}\"");
Console.WriteLine();

// ─── Property set to dump ─────────────────────────────────────────────────────
//
// "Standard" set covers the properties you most commonly care about:
// present value, units, status, reliability, description, name, identifier.
// --all-props tries every known BacnetPropertyIds value.

static BacnetPropertyIds[] StandardProperties() =>
[
    BacnetPropertyIds.PROP_OBJECT_IDENTIFIER,
    BacnetPropertyIds.PROP_OBJECT_NAME,
    BacnetPropertyIds.PROP_OBJECT_TYPE,
    BacnetPropertyIds.PROP_DESCRIPTION,
    BacnetPropertyIds.PROP_PRESENT_VALUE,
    BacnetPropertyIds.PROP_UNITS,
    BacnetPropertyIds.PROP_STATUS_FLAGS,
    BacnetPropertyIds.PROP_EVENT_STATE,
    BacnetPropertyIds.PROP_RELIABILITY,
    BacnetPropertyIds.PROP_OUT_OF_SERVICE,
    BacnetPropertyIds.PROP_COV_INCREMENT,
    BacnetPropertyIds.PROP_PRIORITY_ARRAY,
    BacnetPropertyIds.PROP_RELINQUISH_DEFAULT,
    BacnetPropertyIds.PROP_NUMBER_OF_STATES,
    BacnetPropertyIds.PROP_POLARITY,
    BacnetPropertyIds.PROP_VENDOR_IDENTIFIER,
    BacnetPropertyIds.PROP_VENDOR_NAME,
    BacnetPropertyIds.PROP_MODEL_NAME,
    BacnetPropertyIds.PROP_FIRMWARE_REVISION,
    BacnetPropertyIds.PROP_APPLICATION_SOFTWARE_VERSION,
    BacnetPropertyIds.PROP_PROTOCOL_VERSION,
    BacnetPropertyIds.PROP_PROTOCOL_REVISION,
    BacnetPropertyIds.PROP_MAX_APDU_LENGTH_ACCEPTED,
    BacnetPropertyIds.PROP_SEGMENTATION_SUPPORTED,
    // ── Calendar (OBJECT_CALENDAR) ───────────────────────────────────────────
    // The date-list is the core payload; present-value (above) is the derived bool.
    BacnetPropertyIds.PROP_DATE_LIST,               // (23) date/range/weekday entries
    // ── Schedule (OBJECT_SCHEDULE) ───────────────────────────────────────────
    BacnetPropertyIds.PROP_EFFECTIVE_PERIOD,                      // (32)  validity window
    BacnetPropertyIds.PROP_WEEKLY_SCHEDULE,                       // (123) 7-day time-command program
    BacnetPropertyIds.PROP_EXCEPTION_SCHEDULE,                    // (38)  special-day overrides
    BacnetPropertyIds.PROP_SCHEDULE_DEFAULT,                      // (174) fallback output value
    BacnetPropertyIds.PROP_LIST_OF_OBJECT_PROPERTY_REFERENCES,   // (54)  data points driven
    // ── Alarm / Event / Error (intrinsic reporting on AI, AO, BI, MSI …) ────
    // Threshold-based (analog):
    BacnetPropertyIds.PROP_HIGH_LIMIT,                // (45)  upper alarm threshold
    BacnetPropertyIds.PROP_LOW_LIMIT,                 // (59)  lower alarm threshold
    BacnetPropertyIds.PROP_DEADBAND,                  // (25)  hysteresis around limits
    BacnetPropertyIds.PROP_LIMIT_ENABLE,              // (52)  which limits are active
    // State-based (binary / multi-state):
    BacnetPropertyIds.PROP_ALARM_VALUES,              // (6)   values that trigger alarm
    BacnetPropertyIds.PROP_FAULT_VALUES,              // (39)  values that trigger fault
    // Notification routing:
    BacnetPropertyIds.PROP_NOTIFICATION_CLASS,        // (17)  → NotificationClass object
    BacnetPropertyIds.PROP_EVENT_ENABLE,              // (35)  which transitions notify
    BacnetPropertyIds.PROP_NOTIFY_TYPE,               // (72)  alarm vs. event
    // Current alarm state:
    BacnetPropertyIds.PROP_ACKED_TRANSITIONS,         // (0)   acknowledged transitions
    BacnetPropertyIds.PROP_EVENT_TIME_STAMPS,         // (130) when each state was entered
    // ── OBJECT_NOTIFICATION_CLASS specific ───────────────────────────────────
    BacnetPropertyIds.PROP_PRIORITY,                  // (86)  per-transition priority
    BacnetPropertyIds.PROP_ACK_REQUIRED,              // (1)   which transitions need ack
    BacnetPropertyIds.PROP_RECIPIENT_LIST,            // (102) destination list
    // ── OBJECT_EVENT_ENROLLMENT specific ─────────────────────────────────────
    BacnetPropertyIds.PROP_EVENT_TYPE,                // (37)  algorithm (out-of-range etc.)
    BacnetPropertyIds.PROP_OBJECT_PROPERTY_REFERENCE, // (78)  monitored data point
    // ── Deziko / extensions ──────────────────────────────────────────
    // PROP_STRUCTURED_OBJECT_LIST (209): list of hierarchy-root Structured Views
    //   – present on the DEVICE object in Deziko CC / PXC controllers
    PROP_STRUCTURED_OBJECT_LIST,
    // PROP_SUBORDINATE_LIST (355): children of a Structured View node
    //   – present on every OBJECT_STRUCTURED_VIEW object in the hierarchy
    PROP_SUBORDINATE_LIST,
];

BacnetPropertyIds[] propsToRead = allProps
    ? Enum.GetValues<BacnetPropertyIds>()
    : StandardProperties();

// ─── Dump ─────────────────────────────────────────────────────────────────────
// In Sim JSON mode the DEVICE object must always be present.
if (simJsonOutput)
{
    bool hasDevice = objects.Any(o => o.Id.type == BacnetObjectTypes.OBJECT_DEVICE);
    if (!hasDevice)
    {
        var devOid  = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, foundId);
        var devName = ReadStringProp(client, address, devOid, BacnetPropertyIds.PROP_OBJECT_NAME) ?? devOid.ToString();
        var devDesc = ReadStringProp(client, address, devOid, BacnetPropertyIds.PROP_DESCRIPTION) ?? "";
        objects.Insert(0, (devOid, devName, devDesc));
    }
}

if (simJsonOutput)
    DumpSimJson(client, address, objects, propsToRead, foundId);
else if (jsonOutput)
    DumpJson(client, address, objects, propsToRead);
else
    DumpTable(client, address, objects, propsToRead);

    Info($"Done. Objects dumped: {objects.Count}");

    if (AnomalyTracker.Items.Count > 0)
    {
        Console.Error.WriteLine("\n=== ANOMALY SUMMARY ===");
        
        // Count how many objects supported dynamic property lists
        var dynCount = AnomalyTracker.Items.Count(a => a.Contains("supports dynamic property list"));
        if (dynCount > 0)
            Console.Error.WriteLine($"[*] {dynCount} object(s) provided a dynamic property list (proprietary props captured).");

        // Show actual errors/aborts
        foreach (var a in AnomalyTracker.Items.Distinct().Where(a => !a.Contains("supports dynamic property list")))
            Console.Error.WriteLine($"[!] {a}");
            
        Console.Error.WriteLine("========================\n");
    }


client.Dispose();
return 0;

// ═══════════════════════════════════════════════════════════════════════════════
//  Output renderers
// ═══════════════════════════════════════════════════════════════════════════════

static void DumpTable(
    BacnetClient client, BacnetAddress address,
    List<(BacnetObjectId Id, string Name, string Description)> objects,
    BacnetPropertyIds[] props)
{
    foreach (var (oid, name, desc) in objects)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"┌─ {oid}");
        Console.ResetColor();
        Console.WriteLine($"│  Name        : {name}");
        if (!string.IsNullOrWhiteSpace(desc))
            Console.WriteLine($"│  Description : {desc}");

        var values = ReadAllProperties(client, address, oid, props);

        // Skip OBJECT_NAME / DESCRIPTION / OBJECT_IDENTIFIER – already shown above,
        // and the Deziko specials which get their own rendered section below.
        var skipIds = new HashSet<BacnetPropertyIds>
        {
            BacnetPropertyIds.PROP_OBJECT_NAME,
            BacnetPropertyIds.PROP_DESCRIPTION,
            BacnetPropertyIds.PROP_OBJECT_IDENTIFIER,
            PROP_STRUCTURED_OBJECT_LIST,
            PROP_SUBORDINATE_LIST,
        };

        int shown = 0;
        foreach (var (propId, rawValues) in values.OrderBy(kv => (uint)kv.Key))
        {
            if (skipIds.Contains(propId)) continue;
            string val = FormatValues(rawValues);
            Console.WriteLine($"│  {propId,-38}: {val}");
            shown++;
        }

        // ── Deziko hierarchy block ────────────────────────────────────────────
        // PROP_STRUCTURED_OBJECT_LIST (209) on DEVICE → hierarchy roots
        if (oid.type == BacnetObjectTypes.OBJECT_DEVICE
            && values.TryGetValue(PROP_STRUCTURED_OBJECT_LIST, out var svRoots))
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"│  [Deziko] PROP_STRUCTURED_OBJECT_LIST (209) – hierarchy root(s):");
            Console.ResetColor();
            foreach (var sv in svRoots)
                Console.WriteLine($"│      {sv.Value}");
            shown++;
        }

        // PROP_SUBORDINATE_LIST (355) on STRUCTURED_VIEW → child objects
        if (oid.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW
            && values.TryGetValue(PROP_SUBORDINATE_LIST, out var children))
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"│  [Deziko] PROP_SUBORDINATE_LIST (355) – children:");
            Console.ResetColor();
            foreach (var ch in children)
                Console.WriteLine($"│      {ch.Value}");
            shown++;
        }

        if (shown == 0)
            Console.WriteLine("│  (no properties readable)");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"└{'─',60}");
        Console.ResetColor();
        Console.WriteLine();
    }
}

static void DumpJson(
    BacnetClient client, BacnetAddress address,
    List<(BacnetObjectId Id, string Name, string Description)> objects,
    BacnetPropertyIds[] props)
{
    var root = new List<Dictionary<string, object>>();

    foreach (var (oid, name, desc) in objects)
    {
        var entry = new Dictionary<string, object>
        {
            ["objectId"]   = oid.ToString(),
            ["objectType"] = oid.type.ToString(),
            ["instance"]   = oid.instance,
            ["name"]       = name,
        };
        if (!string.IsNullOrWhiteSpace(desc))
            entry["description"] = desc;

        var propMap = new Dictionary<string, string>();
        var values  = ReadAllProperties(client, address, oid, props);
        foreach (var (propId, rawValues) in values.OrderBy(kv => (uint)kv.Key))
            propMap[propId.ToString()] = FormatValues(rawValues);

        entry["properties"] = propMap;

        // ── Deziko specials: surface as first-class JSON arrays ────────────────
        if (oid.type == BacnetObjectTypes.OBJECT_DEVICE
            && values.TryGetValue(PROP_STRUCTURED_OBJECT_LIST, out var svRoots))
        {
            entry["dezikoStructuredObjectList"] =
                svRoots.Select(v => v.Value?.ToString() ?? "").ToList();
        }
        if (oid.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW
            && values.TryGetValue(PROP_SUBORDINATE_LIST, out var children))
        {
            entry["dezikoSubordinateList"] =
                children.Select(v => v.Value?.ToString() ?? "").ToList();
        }

        root.Add(entry);
    }

    var opts = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(root, opts));
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Simulator JSON renderer  (--sim-json / bacnet-sim input)
// ═══════════════════════════════════════════════════════════════════════════════

static void DumpSimJson(
    BacnetClient client, BacnetAddress address,
    List<(BacnetObjectId Id, string Name, string Description)> objects,
    BacnetPropertyIds[] props,
    uint deviceId)
{
    var simData = new
    {
        meta = new
        {
            deviceId = deviceId,
            generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            description = "Generated by bacnet-dump for bacnet-sim consumption"
        },
        objects = objects.Select(o =>
        {
            var values = ReadAllProperties(client, address, o.Id, props);
            return new
            {
                type = o.Id.type.ToString(),
                instance = o.Id.instance,
                properties = values.OrderBy(kv => (uint)kv.Key).Select(kv => new
                {
                    id = kv.Key.ToString(),
                    tag = kv.Value[0].Tag.ToString(),
                    values = kv.Value.Select(v => SerializeValueForSim(v)).ToList()
                }).ToList()
            };
        }).ToList()
    };

    var opts = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(simData, opts));
}

static object? SerializeValueForSim(BacnetValue v)
{
    if (v.Value is null) return null;

    // 1. Handle Nested BacnetValues or Collections (Schedules, Lists, etc.)
    if (v.Value is BacnetValue nested) return SerializeValueForSim(nested);
    
    if (v.Value is System.Collections.IEnumerable en && !(v.Value is string) && !(v.Value is byte[]))
    {
        var list = new List<object?>();
        foreach (var item in en)
        {
            if (item is BacnetValue bv) list.Add(SerializeValueForSim(bv));
            else list.Add(SerializeValueForSim(new BacnetValue(v.Tag, item)));
        }
        return list;
    }

    // 2. Handle Base Types
    return v.Tag switch
    {
        BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN    => (bool)v.Value,
        BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL       => (float)v.Value,
        BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE     => (double)v.Value,
        BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT => Convert.ToUInt32(v.Value),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT   => Convert.ToInt32(v.Value),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED   => Convert.ToUInt32(v.Value),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE         => ((DateTime)v.Value).ToString("yyyy-MM-dd"),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME         => ((DateTime)v.Value).ToString("HH:mm:ss"),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING   => v.Value.ToString(),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_OCTET_STRING => v.Value is byte[] b ? BitConverter.ToString(b).Replace("-", "") : v.Value.ToString(),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID   => v.Value.ToString() ?? "",
        _ => v.Value is byte[] b ? BitConverter.ToString(b).Replace("-", "") : v.Value.ToString() ?? ""
    };
}


// ═══════════════════════════════════════════════════════════════════════════════
//  BACnet helpers
// ═══════════════════════════════════════════════════════════════════════════════

static List<BacnetObjectId> ReadObjectList(
    BacnetClient client, BacnetAddress address, BacnetObjectId deviceObjId)
{
    var list = new List<BacnetObjectId>();

    // Try bulk read first.
    // Some devices send a BACnet Abort (SEGMENTATION_NOT_SUPPORTED) when the
    // list is too large to fit in one APDU.  The library throws in that case
    // rather than returning false, so we catch it and fall through.
    try
    {
        if (client.ReadPropertyRequest(address, deviceObjId,
                BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> bulk))
        {
            foreach (var v in bulk)
                if (v.Value is BacnetObjectId oid)
                    list.Add(oid);
            return list;
        }
    }
    catch (Exception ex)
    {
        Warn($"Bulk PROP_OBJECT_LIST read failed ({ex.Message}) – switching to indexed fallback…");
        AnomalyTracker.Track($"Object list bulk read failed: {ex.Message}");
    }

    // Indexed fallback: read count at array index 0, then each entry by 1-based index.
    // Per BACnet spec §12.11.7 the array index 0 always returns the number of entries.
    Warn("Reading PROP_OBJECT_LIST entry-by-entry (device does not support bulk read)…");

    IList<BacnetValue>? countVals = null;
    try
    {
        client.ReadPropertyRequest(address, deviceObjId,
            BacnetPropertyIds.PROP_OBJECT_LIST, out countVals, arrayIndex: 0);
    }
    catch (Exception ex)
    {
        Error($"Cannot read PROP_OBJECT_LIST count: {ex.Message}");
        return list;
    }

    if (countVals == null || countVals.Count == 0)
    {
        Error("Cannot read PROP_OBJECT_LIST count: empty response.");
        return list;
    }

    uint count = Convert.ToUInt32(countVals[0].Value);
    Info($"PROP_OBJECT_LIST: {count} entries – reading individually…");

    for (uint i = 1; i <= count; i++)
    {
        try
        {
            if (client.ReadPropertyRequest(address, deviceObjId,
                    BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> entry, arrayIndex: i)
                && entry.Count > 0
                && entry[0].Value is BacnetObjectId eid)
            {
                list.Add(eid);
            }
        }
        catch (Exception ex)
        {
            Warn($"  index {i}: skipped ({ex.Message})");
        }
    }
    return list;
}

/// <summary>Read a single string property. Returns null on failure.</summary>
static string? ReadStringProp(
    BacnetClient client, BacnetAddress address,
    BacnetObjectId oid, BacnetPropertyIds propId)
{
    try
    {
        if (client.ReadPropertyRequest(address, oid, propId, out IList<BacnetValue> vals)
            && vals.Count > 0)
            return vals[0].Value?.ToString();
    }
    catch { /* swallow */ }
    return null;
}

/// <summary>
/// Reads a set of properties from one object.
/// Tries ReadPropertyMultiple first; falls back to one-by-one if that fails.
/// Returns only the properties that were successfully read.
/// </summary>
static Dictionary<BacnetPropertyIds, IList<BacnetValue>> ReadAllProperties(
    BacnetClient client, BacnetAddress address,
    BacnetObjectId oid, BacnetPropertyIds[] defaultPropIds)
{
    var result = new Dictionary<BacnetPropertyIds, IList<BacnetValue>>();
    
    // 1. Discover all supported properties via PROP_PROPERTY_LIST (371)
    // This allows us to find proprietary/manufacturer-specific properties.
    var propIds = defaultPropIds.ToList();
    try
    {
        if (client.ReadPropertyRequest(address, oid, (BacnetPropertyIds)371, out IList<BacnetValue> listVals))
        {
            AnomalyTracker.Track($"Device supports dynamic property list on {oid}");
            foreach (var v in listVals)
            {
                if (v.Value is uint pid && !propIds.Contains((BacnetPropertyIds)pid))
                    propIds.Add((BacnetPropertyIds)pid);
            }
        }
    }
    catch { /* Not supported */ }

    // 2. Try bulk read using PROP_ALL (8) - fastest if the device supports it
    try 
    {
        var allReq = new List<BacnetReadAccessSpecification> {
            new(oid, new List<BacnetPropertyReference> { new((uint)BacnetPropertyIds.PROP_ALL, uint.MaxValue) })
        };
        if (client.ReadPropertyMultipleRequest(address, allReq, out var allResults) && allResults.Count > 0)
        {
            foreach (var pv in allResults[0].values)
            {
                var pid = (BacnetPropertyIds)pv.property.propertyIdentifier;
                if (pv.value != null && pv.value.Count > 0 && pv.value[0].Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR)
                    result[pid] = pv.value;
            }
            if (result.Count > 5) return result; 
        }
    } catch { }

    // 3. Chunked RPM read (to avoid APDU size limits while remaining efficient)
    int chunkSize = 15; 
    for (int i = 0; i < propIds.Count; i += chunkSize)
    {
        var chunk = propIds.Skip(i).Take(chunkSize).ToList();
        var refs = chunk.Select(p => new BacnetPropertyReference((uint)p, uint.MaxValue)).ToList();
        var rpmReq = new List<BacnetReadAccessSpecification> { new BacnetReadAccessSpecification(oid, refs) };

        bool chunkSuccess = false;
        var chunkResults = new List<BacnetReadAccessResult>();
        try
        {
            chunkSuccess = client.ReadPropertyMultipleRequest(address, rpmReq, out var rawResults);
            if (chunkSuccess) chunkResults = rawResults.ToList();
        }
        catch (Exception ex)
        {
            AnomalyTracker.Track($"RPM chunk failed on {oid}: {ex.Message}");
            chunkSuccess = false; 
        }

        if (chunkSuccess)
        {
            foreach (var res in chunkResults)
            foreach (var pv in res.values)
            {
                var pid = (BacnetPropertyIds)pv.property.propertyIdentifier;
                if (pv.value != null && pv.value.Count > 0)
                {
                    if (pv.value[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR)
                    {
                        var err = (BacnetError)pv.value[0].Value!;
                        if (err.error_code == BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY) continue;
                        AnomalyTracker.Track($"{oid} {pid} failed: {err.error_class}/{err.error_code}");
                    }
                    result[pid] = pv.value;
                }
            }
        }
        else
        {
            // Fallback to one-by-one for this chunk if the device doesn't like bulk requests
            foreach (var pid in chunk)
            {
                try {
                    if (client.ReadPropertyRequest(address, oid, pid, out var vals) && vals.Count > 0)
                    {
                        if (vals[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR) continue;
                        result[pid] = vals;
                    }
                } catch { }
            }
        }
    }

    return result;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Formatting helpers
// ═══════════════════════════════════════════════════════════════════════════════

static string FormatValues(IList<BacnetValue> values)
{
    if (values == null || values.Count == 0) return "(empty)";

    if (values.Count == 1)
        return FormatSingle(values[0]);

    // Multi-value: comma-separated list (e.g. priority array)
    var sb = new StringBuilder("[");
    for (int i = 0; i < values.Count; i++)
    {
        if (i > 0) sb.Append(", ");
        sb.Append(FormatSingle(values[i]));
        if (i >= 15 && values.Count > 17)
        {
            sb.Append($", … (+{values.Count - i - 1} more)");
            break;
        }
    }
    sb.Append(']');
    return sb.ToString();
}

static string FormatSingle(BacnetValue v)
{
    if (v.Value is null) return "null";

    return v.Tag switch
    {
        BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL          => $"{v.Value:F4}",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE        => $"{v.Value:F6}",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID    => $"{v.Value}",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING    => $"{v.Value}",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN       => ((bool)v.Value ? "true" : "false"),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED    =>
            v.Value is BacnetObjectTypes bot ? bot.ToString()
            : v.Value?.ToString() ?? "?",
        BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR =>
            v.Value is BacnetError err ? $"Error: {err.error_class} / {err.error_code}"
            : "Error: (unknown)",
        _ => v.Value?.ToString() ?? "?",
    };
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Wildcard → Regex
// ═══════════════════════════════════════════════════════════════════════════════

static Regex WildcardToRegex(string pattern)
{
    string escaped = "^"
        + Regex.Escape(pattern)
               .Replace(@"\*", ".*")
               .Replace(@"\?", ".")
        + "$";
    return new Regex(escaped, RegexOptions.IgnoreCase | RegexOptions.Singleline);
}

// ═══════════════════════════════════════════════════════════════════════════════
//  DNS helper
// ═══════════════════════════════════════════════════════════════════════════════

static string ResolveHost(string host)
{
    if (IPAddress.TryParse(host, out _)) return host;
    try
    {
        var addrs = Dns.GetHostAddresses(host);
        var v4 = addrs.FirstOrDefault(
            a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (v4 != null) return v4.ToString();
    }
    catch (Exception ex) { Warn($"DNS lookup failed for '{host}': {ex.Message}"); }
    return host;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Console helpers
// ═══════════════════════════════════════════════════════════════════════════════

static void Info(string msg)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Error.WriteLine($"[info] {msg}");
    Console.ResetColor();
}

static void Warn(string msg)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Error.WriteLine($"[warn] {msg}");
    Console.ResetColor();
}

static void Error(string msg)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[error] {msg}");
    Console.ResetColor();
}

static void PrintUsage()
{
    Console.WriteLine("""
bacnet-dump – dump all objects and properties from a BACnet/IP device

Usage:
  bacnet-dump <host[:port]> [<device-id>] [OPTIONS]

Arguments:
  host[:port]   IP address or hostname of the BACnet device
                Append :port to use a non-standard UDP port (default 47808)
  device-id     BACnet device instance number (optional; auto-detected via Who-Is)

Options:
  --filter <wildcard>   Name wildcard filter (default: "*" = all objects)
                        Matches against PROP_OBJECT_NAME.
                        Wildcards: * = any chars, ? = any single char
  --timeout <ms>        Who-Is wait timeout in milliseconds (default: 3000)
  --port <n>            Local UDP port for the client socket (default: OS-assigned)
  --json                Output as pretty-printed JSON instead of a table
  --sim-json            Output as structured JSON (bacnet-sim device.json format)
                        Always includes the DEVICE object regardless of --filter.
                        Redirect to file: bacnet-dump ... --sim-json > device.json
  --all-props           Try every known BACnet property identifier (slow!)
  -h, --help            Show this help

Examples:
  bacnet-dump 192.168.1.10
  bacnet-dump 192.168.1.10 1001
  bacnet-dump 192.168.1.10:47808 1001 --filter "AI*"
  bacnet-dump localhost 1001 --filter "*Temp*"
  bacnet-dump localhost 1001 --json > dump.json
  bacnet-dump localhost 1001 --sim-json > device.json  # use as bacnet-sim input
  bacnet-dump localhost 1001 --filter "*" --all-props
  bacnet-dump localhost 1001 --filter "BI*" --json
""");
}

public static class AnomalyTracker 
{
    public static List<string> Items = new();
    public static void Track(string msg) { lock(Items) Items.Add(msg); }
}
