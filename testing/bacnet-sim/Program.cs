// Program.cs – BACnet/IP simulator (Lalatec-Deziko-style)
// Data-driven from device.json (produced by bacnet-dump --sim-json)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

const int    PORT      = 47808;
const double PERIOD    = 300.0;   // 5-min sine

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── JSON Device Storage ───────────────────────────────────────────────────────
string jsonPath = Path.Combine(AppContext.BaseDirectory, "device.json");
if (!File.Exists(jsonPath))
{
    Console.Error.WriteLine($"Error: {jsonPath} not found. Run bacnet-dump with --sim-json first.");
    return;
}

var jsonDoc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
var root    = jsonDoc.RootElement;
uint DEVICE_ID = root.GetProperty("meta").GetProperty("deviceId").GetUInt32();

// Flattened storage: Dictionary<"TYPE:INSTANCE", Dictionary<BacnetPropertyIds, List<BacnetValue>>>
var storage = new Dictionary<string, Dictionary<BacnetPropertyIds, List<BacnetValue>>>();

foreach (var obj in root.GetProperty("objects").EnumerateArray())
{
    var typeStr = obj.GetProperty("type").GetString() ?? "OBJECT_DEVICE";
    var inst    = obj.GetProperty("instance").GetUInt32();
    var type    = Enum.Parse<BacnetObjectTypes>(typeStr);
    var key     = $"{type}:{inst}";

    var props = new Dictionary<BacnetPropertyIds, List<BacnetValue>>();
    foreach (var prop in obj.GetProperty("properties").EnumerateArray())
    {
        var idStr  = prop.GetProperty("id").GetString() ?? "PROP_OBJECT_NAME";
        var tagStr = prop.GetProperty("tag").GetString() ?? "BACNET_APPLICATION_TAG_CHARACTER_STRING";
        var id     = Enum.Parse<BacnetPropertyIds>(idStr);
        var tag    = Enum.Parse<BacnetApplicationTags>(tagStr);

        var values = new List<BacnetValue>();
        foreach (var val in prop.GetProperty("values").EnumerateArray())
        {
            values.Add(ParseJsonValue(val, tag));
        }
        props[id] = values;
    }
    storage[key] = props;
}

static BacnetValue ParseJsonValue(JsonElement el, BacnetApplicationTags tag)
{
    if (el.ValueKind == JsonValueKind.Null) return new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
    
    if (el.ValueKind == JsonValueKind.Array)
    {
        var list = new List<object?>();
        foreach (var item in el.EnumerateArray())
            list.Add(ParseJsonValue(item, tag).Value);
        return new BacnetValue(tag, list);
    }

    object val = tag switch
    {
        BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN    => el.GetBoolean(),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL       => (float)el.GetDouble(),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE     => el.GetDouble(),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT => el.GetUInt32(),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT   => el.GetInt32(),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED   => el.GetUInt32(),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE         => DateTime.Parse(el.GetString() ?? "0001-01-01"),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME         => DateTime.Parse(el.GetString() ?? "00:00:00"),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING   => BacnetBitString.Parse(el.GetString() ?? ""),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_OCTET_STRING => HexToBytes(el.GetString() ?? ""),
        BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID   => BacnetObjectId.Parse(el.GetString() ?? ""),
        _ => el.GetString() ?? ""
    };
    return new BacnetValue(tag, val);
}

static byte[] HexToBytes(string hex)
{
    if (hex.Length % 2 != 0) return Array.Empty<byte>();
    byte[] bytes = new byte[hex.Length / 2];
    for (int i = 0; i < bytes.Length; i++)
        bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
    return bytes;
}

// ── Derived Metadata ──────────────────────────────────────────────────────────
var allObjectIds = storage.Keys.Select(BacnetObjectId.Parse).ToArray();
var deviceObjId  = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, DEVICE_ID);

// ── COV subscription store ────────────────────────────────────────────────────
var covSubs = new Dictionary<string, List<CovSub>>();
var covLock = new object();

// ── BACnet client ─────────────────────────────────────────────────────────────
var transport = new BacnetIpUdpProtocolTransport(PORT, useExclusivePort: true);
var client    = new BacnetClient(transport);

client.OnWhoIs += (sender, adr, lo, hi) =>
{
    if (lo == -1 || (lo <= DEVICE_ID && DEVICE_ID <= (uint)hi))
    {
        sender.Iam(DEVICE_ID, BacnetSegmentations.SEGMENTATION_NONE, null, null);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Who-Is → I-Am to {adr}");
    }
};

client.OnReadPropertyRequest += (sender, adr, invokeId, objectId, property, _) =>
{
    var propId = (BacnetPropertyIds)property.propertyIdentifier;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] REQ {objectId}  prop={propId}  idx={property.propertyArrayIndex}  from={adr}");
    
    try
    {
        // Special Handling for lists that may need indexing
        if (propId == BacnetPropertyIds.PROP_OBJECT_LIST && objectId.type == BacnetObjectTypes.OBJECT_DEVICE)
        {
            ServeList(sender, adr, invokeId, objectId, property, allObjectIds.Select(id => new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, id)).ToList());
            return;
        }

        if (storage.TryGetValue(objectId.ToString(), out var props) && props.TryGetValue(propId, out var values))
        {
            // If it's a list request
            if (property.propertyArrayIndex != uint.MaxValue)
            {
                if (property.propertyArrayIndex == 0)
                {
                    sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, new List<BacnetValue> { new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, (uint)values.Count) });
                }
                else
                {
                    int idx = (int)property.propertyArrayIndex - 1;
                    if (idx >= 0 && idx < values.Count)
                        sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, new List<BacnetValue> { values[idx] });
                    else
                        sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invokeId, BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_INVALID_ARRAY_INDEX);
                }
            }
            else
            {
                sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, values);
            }
            return;
        }

        sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invokeId, BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  => HANDLER EXCEPTION: {ex}");
    }
};

void ServeList(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, BacnetPropertyReference property, IList<BacnetValue> allValues)
{
    if (property.propertyArrayIndex == 0)
    {
        sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, new List<BacnetValue> { new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, (uint)allValues.Count) });
    }
    else if (property.propertyArrayIndex == uint.MaxValue)
    {
        sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, allValues);
    }
    else
    {
        int idx = (int)property.propertyArrayIndex - 1;
        if (idx >= 0 && idx < allValues.Count)
            sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, new List<BacnetValue> { allValues[idx] });
        else
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invokeId, BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_INVALID_ARRAY_INDEX);
    }
}

client.OnReadPropertyMultipleRequest += (sender, adr, invokeId, props, _) =>
{
    var results = new List<BacnetReadAccessResult>();
    foreach (var req in props)
    {
        var pvList = new List<BacnetPropertyValue>();
        storage.TryGetValue(req.objectIdentifier.ToString(), out var objProps);

        foreach (var pref in req.propertyReferences)
        {
            var propId = (BacnetPropertyIds)pref.propertyIdentifier;
            IList<BacnetValue>? vals = null;

            if (propId == BacnetPropertyIds.PROP_OBJECT_LIST && req.objectIdentifier.type == BacnetObjectTypes.OBJECT_DEVICE)
            {
                vals = allObjectIds.Select(id => new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, id)).ToList();
            }
            else if (objProps != null && objProps.TryGetValue(propId, out var storedVals))
            {
                vals = storedVals;
            }

            pvList.Add(new BacnetPropertyValue
            {
                property = pref,
                value    = (IList<BacnetValue>?)vals ?? new List<BacnetValue>
                {
                    new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR, new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY))
                }
            });
        }
        results.Add(new BacnetReadAccessResult(req.objectIdentifier, pvList));
    }
    sender.ReadPropertyMultipleResponse(adr, invokeId, default, results);
};

client.OnWritePropertyRequest += (sender, adr, invokeId, objectId, value, _) =>
{
    var propId = (BacnetPropertyIds)value.property.propertyIdentifier;
    if (storage.TryGetValue(objectId.ToString(), out var props))
    {
        props[propId] = value.value.ToList();
        sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WRITE {objectId} {propId} = {value.value.FirstOrDefault().Value}");
        NotifyCov(objectId);
        return;
    }
    sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId, BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
};

client.OnSubscribeCOV += (sender, adr, invokeId, processId, objectId, cancel, confirmed, lifetime, _) =>
{
    string key = objectId.ToString();
    lock (covLock)
    {
        if (!covSubs.ContainsKey(key)) covSubs[key] = new List<CovSub>();
        var list = covSubs[key];
        list.RemoveAll(s => s.Adr.ToString() == adr.ToString() && s.ProcessId == processId);
        if (!cancel)
        {
            list.Add(new CovSub(adr, processId, confirmed, DateTime.UtcNow.AddSeconds(lifetime)));
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] COV+ {objectId}  from {adr}  life={lifetime}s");
        }
        else Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] COV- {objectId}  from {adr}");
    }
    sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, invokeId);
};

void NotifyCov(BacnetObjectId objectId)
{
    string key = objectId.ToString();
    List<CovSub> subs;
    lock (covLock)
    {
        if (!covSubs.TryGetValue(key, out var raw)) return;
        raw.RemoveAll(s => DateTime.UtcNow > s.ExpiresAt);
        subs = raw.ToList();
    }
    if (subs.Count == 0) return;

    if (!storage.TryGetValue(key, out var props) || !props.TryGetValue(BacnetPropertyIds.PROP_PRESENT_VALUE, out var pvVals)) return;
    props.TryGetValue(BacnetPropertyIds.PROP_STATUS_FLAGS, out var sfVals);

    var covValues = new List<BacnetPropertyValue>
    {
        new() { property = new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_PRESENT_VALUE, uint.MaxValue), value = pvVals },
        new() { property = new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_STATUS_FLAGS, uint.MaxValue), value = sfVals ?? new List<BacnetValue>() },
    };

    foreach (var sub in subs)
    {
        uint remaining = (uint)Math.Max(0, (sub.ExpiresAt - DateTime.UtcNow).TotalSeconds);
        client.Notify(sub.Adr, sub.ProcessId, DEVICE_ID, objectId, remaining, sub.Confirmed, covValues);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] COV-> {objectId}  val={pvVals[0].Value}  to={sub.Adr}");
    }
}

client.Start();
Console.WriteLine($"=== BACnet/IP Simulator (Data-Driven) device={DEVICE_ID} ===");
Console.WriteLine($"Listening on UDP 0.0.0.0:{PORT}  |  objects={storage.Count}");
client.Iam(DEVICE_ID, BacnetSegmentations.SEGMENTATION_NONE, null, null);

// ── Update loop ───────────────────────────────────────────────────────────────
var t0 = DateTime.UtcNow;
await Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(5_000, cts.Token).ConfigureAwait(false);
        double e = (DateTime.UtcNow - t0).TotalSeconds;

        foreach (var oid in allObjectIds)
        {
            if (!storage.TryGetValue(oid.ToString(), out var props)) continue;
            if (!props.ContainsKey(BacnetPropertyIds.PROP_PRESENT_VALUE)) continue;

            bool changed = false;
            if (oid.type is BacnetObjectTypes.OBJECT_ANALOG_INPUT or BacnetObjectTypes.OBJECT_ANALOG_OUTPUT or BacnetObjectTypes.OBJECT_ANALOG_VALUE)
            {
                float lo = 10, hi = 30;
                float v = lo + (hi - lo) * (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * (e + oid.instance * 10) / PERIOD));
                props[BacnetPropertyIds.PROP_PRESENT_VALUE] = new List<BacnetValue> { new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, v) };
                changed = true;
            }
            else if (oid.type is BacnetObjectTypes.OBJECT_BINARY_INPUT or BacnetObjectTypes.OBJECT_BINARY_OUTPUT or BacnetObjectTypes.OBJECT_BINARY_VALUE)
            {
                uint v = (e + oid.instance * 5) % 20 < 10 ? 1u : 0u;
                props[BacnetPropertyIds.PROP_PRESENT_VALUE] = new List<BacnetValue> { new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, v) };
                changed = true;
            }
            else if (oid.type is BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT or BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE)
            {
                uint v = ((uint)(e + oid.instance * 2) / 10 % 3) + 1;
                props[BacnetPropertyIds.PROP_PRESENT_VALUE] = new List<BacnetValue> { new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, v) };
                changed = true;
            }

            if (changed) NotifyCov(oid);
        }
    }
}, cts.Token);

client.Dispose();
record CovSub(BacnetAddress Adr, uint ProcessId, bool Confirmed, DateTime ExpiresAt);
