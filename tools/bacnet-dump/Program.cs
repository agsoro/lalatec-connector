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
bool    jsonOutput = false;
bool    allProps   = false;

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

        case "--json":      jsonOutput = true; break;
        case "--all-props": allProps   = true; break;

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
BacnetAddress? address  = null;
uint           foundId  = 0;

using (var signal = new ManualResetEventSlim(false))
{
    void OnIam(BacnetClient _, BacnetAddress adr, uint id,
               uint maxApdu, BacnetSegmentations seg, ushort vendor)
    {
        // Accept any device or the specific requested one
        if (deviceId.HasValue && id != deviceId.Value) return;
        if (address != null) return;   // take first match
        address = adr;
        foundId = id;
        signal.Set();
    }

    client.OnIam += OnIam;
    client.WhoIs();
    signal.Wait(whoIsMs);
    client.OnIam -= OnIam;
}

if (address == null)
{
    // Fallback: construct address directly from IP:port (works for unicast).
    // BacnetAddress throws if ip is not a valid dotted-decimal address,
    // so we validate here and give a clean error message.
    if (!System.Net.IPAddress.TryParse(ip, out _))
    {
        Error($"'{host}' could not be resolved to a valid IP address.");
        Error("Try: bacnet-dump --help");
        client.Dispose();
        return 1;
    }
    address = new BacnetAddress(BacnetAddressTypes.IP, $"{ip}:{remPort}");
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
if (jsonOutput)
    DumpJson(client, address, objects, propsToRead);
else
    DumpTable(client, address, objects, propsToRead);

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
//  BACnet helpers
// ═══════════════════════════════════════════════════════════════════════════════

static List<BacnetObjectId> ReadObjectList(
    BacnetClient client, BacnetAddress address, BacnetObjectId deviceObjId)
{
    var list = new List<BacnetObjectId>();

    // Try bulk read first
    if (client.ReadPropertyRequest(address, deviceObjId,
            BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> bulk))
    {
        foreach (var v in bulk)
            if (v.Value is BacnetObjectId oid)
                list.Add(oid);
        return list;
    }

    // Segmented fallback: read count at index 0, then each entry
    Warn("Bulk PROP_OBJECT_LIST failed – using indexed fallback…");
    if (!client.ReadPropertyRequest(address, deviceObjId,
            BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> countVals, arrayIndex: 0))
    {
        Error("Cannot read PROP_OBJECT_LIST count.");
        return list;
    }

    uint count = Convert.ToUInt32(countVals[0].Value);
    for (uint i = 1; i <= count; i++)
    {
        if (client.ReadPropertyRequest(address, deviceObjId,
                BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> entry, arrayIndex: i)
            && entry.Count > 0
            && entry[0].Value is BacnetObjectId eid)
        {
            list.Add(eid);
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
    BacnetObjectId oid, BacnetPropertyIds[] propIds)
{
    var result = new Dictionary<BacnetPropertyIds, IList<BacnetValue>>();

    // Build RPM request
    var propRefs = propIds
        .Select(p => new BacnetPropertyReference((uint)p, uint.MaxValue))
        .ToList();
    var readReq = new List<BacnetReadAccessSpecification>
    {
        new BacnetReadAccessSpecification(oid, propRefs)
    };

    if (client.ReadPropertyMultipleRequest(address, readReq, out IList<BacnetReadAccessResult> rpmResults))
    {
        foreach (var res in rpmResults)
        {
            foreach (var pv in res.values)
            {
                var pid = (BacnetPropertyIds)pv.property.propertyIdentifier;
                // Ignore error responses
                if (pv.value != null && pv.value.Count > 0
                    && pv.value[0].Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR)
                {
                    result[pid] = pv.value;
                }
            }
        }
        return result;
    }

    // Fallback: one property at a time
    foreach (var propId in propIds)
    {
        try
        {
            if (client.ReadPropertyRequest(address, oid, propId, out IList<BacnetValue> vals)
                && vals.Count > 0
                && vals[0].Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR)
            {
                result[propId] = vals;
            }
        }
        catch { /* property not available */ }
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
  --all-props           Try every known BACnet property identifier (slow!)
  -h, --help            Show this help

Examples:
  bacnet-dump 192.168.1.10
  bacnet-dump 192.168.1.10 1001
  bacnet-dump 192.168.1.10:47808 1001 --filter "AI*"
  bacnet-dump localhost 1001 --filter "*Temp*"
  bacnet-dump localhost 1001 --json > dump.json
  bacnet-dump localhost 1001 --filter "*" --all-props
  bacnet-dump localhost 1001 --filter "BI*" --json
""");
}
