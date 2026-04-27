// Program.cs – BACnet/IP simulator (Deziko-style, device 1001)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.IO.BACnet.Storage;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ErrorCodes = System.IO.BACnet.Storage.DeviceStorage.ErrorCodes;

const uint   DEVICE_ID = 1001;
const int    PORT      = 47808;
const double PERIOD    = 300.0;   // 5-min sine

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Device storage ────────────────────────────────────────────────────────────
string xmlPath = Path.Combine(AppContext.BaseDirectory, "device.xml");
var storage     = DeviceStorage.Load(xmlPath, DEVICE_ID);
var deviceObjId = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, DEVICE_ID);

// Explicit object list – DeviceStorage can't round-trip multi-value XML arrays
// back to BacnetObjectId, so we maintain this list in code and serve it directly.
var allObjectIds = new BacnetObjectId[]
{
    new(BacnetObjectTypes.OBJECT_DEVICE,             DEVICE_ID),
    new(BacnetObjectTypes.OBJECT_ANALOG_INPUT,       1),
    new(BacnetObjectTypes.OBJECT_ANALOG_INPUT,       2),
    new(BacnetObjectTypes.OBJECT_ANALOG_INPUT,       3),
    new(BacnetObjectTypes.OBJECT_ANALOG_INPUT,       4),
    new(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT,      1),
    new(BacnetObjectTypes.OBJECT_ANALOG_VALUE,       1),
    new(BacnetObjectTypes.OBJECT_ANALOG_VALUE,       2),
    new(BacnetObjectTypes.OBJECT_BINARY_INPUT,       1),
    new(BacnetObjectTypes.OBJECT_BINARY_OUTPUT,      1),
    new(BacnetObjectTypes.OBJECT_BINARY_VALUE,       1),
    new(BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT,  1),
    new(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, 1),
    new(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE,  1),
    // Structured Views – filtered out of COV/poll but present in PROP_OBJECT_LIST
    new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW,    1),  // AHU_01
    new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW,    2),  // Air_Supply
    new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW,    3),  // Air_Return
    new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW,    4),  // Control_Loop
    new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW,    5),  // Unit1
    new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW,    6),  // Room101
};

// ── Deziko hierarchy ──────────────────────────────────────────────────────────
const BacnetPropertyIds PROP_STRUCTURED_OBJECT_LIST = (BacnetPropertyIds)209;
const BacnetPropertyIds PROP_SUBORDINATE_LIST        = (BacnetPropertyIds)355;

// PROP_STRUCTURED_OBJECT_LIST on the Device: SV:5 (Unit1) is the single root
var svRoots = new BacnetObjectId[]
{
    new(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW, 5),
};

// PROP_SUBORDINATE_LIST for each Structured View object
// Hierarchy:  Unit1 (SV:5) → Room101 (SV:6) → AHU_01 (SV:1)
//                                                 ├─ Air_Supply   (SV:2)
//                                                 ├─ Air_Return   (SV:3)
//                                                 └─ Control_Loop (SV:4)
var svChildren = new Dictionary<uint, BacnetObjectId[]>
{
    // SV:5 – Unit1  → Room101
    [5] = new[] { new BacnetObjectId(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW, 6) },
    // SV:6 – Room101 → AHU_01
    [6] = new[] { new BacnetObjectId(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW, 1) },
    // SV:1 – AHU_01  → three sub-folders
    [1] = new[]
    {
        new BacnetObjectId(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW, 2),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW, 3),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_STRUCTURED_VIEW, 4),
    },
    // SV:2 – Air_Supply  → supply temp, outdoor temp, fan speed
    [2] = new[]
    {
        new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT,  1),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT,  2),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1),
    },
    // SV:3 – Air_Return  → supply duct temp, return duct temp
    [3] = new[]
    {
        new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 3),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 4),
    },
    // SV:4 – Control_Loop → all binary/multi-state + setpoint/load values
    [4] = new[]
    {
        new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_INPUT,       1),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_OUTPUT,      1),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE,       1),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE,       1),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE,       2),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT,  1),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, 1),
        new BacnetObjectId(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE,  1),
    },
};

// ── COV subscription store ────────────────────────────────────────────────────
var covSubs = new Dictionary<string, List<CovSub>>();
var covLock = new object();

// ── Runtime present-value store ──────────────────────────────────────────────
// DeviceStorage.WriteProperty does not reliably update its readable storage
// (see comment above regarding PROP_OBJECT_LIST serialisation issues).
// We therefore maintain our own in-memory table and serve PROP_PRESENT_VALUE
// directly from it in both ReadProperty and ReadPropertyMultiple handlers.
var presentValues = new Dictionary<string, BacnetValue>
{
    ["OBJECT_ANALOG_INPUT:1"]       = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,          21.5f),
    ["OBJECT_ANALOG_INPUT:2"]       = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,          10.0f),
    ["OBJECT_ANALOG_INPUT:3"]       = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,          16.0f),
    ["OBJECT_ANALOG_INPUT:4"]       = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,          22.0f),
    ["OBJECT_ANALOG_OUTPUT:1"]      = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,          50.0f),
    ["OBJECT_ANALOG_VALUE:1"]       = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,          21.0f),
    ["OBJECT_ANALOG_VALUE:2"]       = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,          30.0f),
    ["OBJECT_BINARY_INPUT:1"]       = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED,    1u),
    ["OBJECT_BINARY_OUTPUT:1"]      = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED,    1u),
    ["OBJECT_BINARY_VALUE:1"]       = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED,    0u),
    ["OBJECT_MULTI_STATE_INPUT:1"]  = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT,  2u),
    ["OBJECT_MULTI_STATE_OUTPUT:1"] = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT,  2u),
    ["OBJECT_MULTI_STATE_VALUE:1"]  = new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT,  1u),
};

IList<BacnetValue>? GetPresentValue(BacnetObjectId oid)
{
    if (presentValues.TryGetValue(oid.ToString(), out var pv))
        return new List<BacnetValue> { pv };
    return null;
}

// ── BACnet client ─────────────────────────────────────────────────────────────
// useExclusivePort:true → one socket for both send AND receive.
// With false the library opens a second ephemeral socket for sending; some
// BACnet clients filter responses that don't come from the well-known port 47808.
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
        // PROP_OBJECT_LIST: serve directly from the typed list.
        // DeviceStorage stores this as a space-separated string in XML and cannot
        // deserialise it back to individual BacnetObjectId values.
        if (objectId.type == BacnetObjectTypes.OBJECT_DEVICE &&
            propId == BacnetPropertyIds.PROP_OBJECT_LIST)
        {
            IList<BacnetValue> values;
            if (property.propertyArrayIndex == 0)            // [0] = count
            {
                values = new List<BacnetValue>
                {
                    new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT,
                        (uint)allObjectIds.Length)
                };
            }
            else if (property.propertyArrayIndex == uint.MaxValue)  // all
            {
                values = allObjectIds
                    .Select(id => new BacnetValue(
                        BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, id))
                    .ToList<BacnetValue>();
            }
            else                                             // [i] = 1-based index
            {
                int idx = (int)property.propertyArrayIndex - 1;
                values = idx >= 0 && idx < allObjectIds.Length
                    ? new List<BacnetValue>
                      {
                          new(BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID,
                              allObjectIds[idx])
                      }
                    : new List<BacnetValue>();
            }
            sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, values);
            Console.WriteLine($"  => OK {values.Count} value(s) → {adr}");
            return;
        }

        // PROP_STRUCTURED_OBJECT_LIST (209): top-level Structured View roots.
        if (objectId.type == BacnetObjectTypes.OBJECT_DEVICE &&
            propId == PROP_STRUCTURED_OBJECT_LIST)
        {
            var values = svRoots
                .Select(id => new BacnetValue(
                    BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, id))
                .ToList<BacnetValue>();
            sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, values);
            Console.WriteLine($"  => OK {values.Count} SV root(s) → {adr}");
            return;
        }

        // PROP_SUBORDINATE_LIST (355): children of a Structured View.
        if (objectId.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW &&
            propId == PROP_SUBORDINATE_LIST)
        {
            IList<BacnetValue> values;
            if (svChildren.TryGetValue(objectId.instance, out var children))
                values = children
                    .Select(id => new BacnetValue(
                        BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, id))
                    .ToList<BacnetValue>();
            else
                values = new List<BacnetValue>();
            sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, values);
            Console.WriteLine($"  => OK {values.Count} child(ren) SV:{objectId.instance} → {adr}");
            return;
        }

        // PROP_PRESENT_VALUE: serve from our in-memory runtime table
        if (propId == BacnetPropertyIds.PROP_PRESENT_VALUE)
        {
            var pv = GetPresentValue(objectId);
            if (pv != null)
            {
                sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, pv);
                return;
            }
            // Object not found in our table – fall through to DeviceStorage
        }

        // All other properties: delegate to DeviceStorage
        var err = storage.ReadProperty(objectId, propId,
            property.propertyArrayIndex, out IList<BacnetValue>? values2);

        if (err != ErrorCodes.Good || values2 == null)
        {
            sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY,
                invokeId,
                err == ErrorCodes.UnknownObject
                    ? BacnetErrorClasses.ERROR_CLASS_OBJECT
                    : BacnetErrorClasses.ERROR_CLASS_PROPERTY,
                err == ErrorCodes.UnknownObject
                    ? BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT
                    : BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
            return;
        }
        sender.ReadPropertyResponse(adr, invokeId, default, objectId, property, values2);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  => HANDLER EXCEPTION for {propId} on {objectId}: {ex}");
    }
};

client.OnReadPropertyMultipleRequest += (sender, adr, invokeId, props, _) =>
{
    var results = new List<BacnetReadAccessResult>();

    foreach (var req in props)
    {
        var pvList = new List<BacnetPropertyValue>();

        foreach (var pref in req.propertyReferences)
        {
            var propId = (BacnetPropertyIds)pref.propertyIdentifier;

            // Serve PROP_PRESENT_VALUE from our runtime table first
            IList<BacnetValue>? vals = null;
            if (propId == BacnetPropertyIds.PROP_PRESENT_VALUE)
                vals = GetPresentValue(req.objectIdentifier);

            // Fall back to DeviceStorage for everything else
            if (vals == null)
            {
                var err2 = storage.ReadProperty(req.objectIdentifier, propId,
                    pref.propertyArrayIndex, out IList<BacnetValue>? sv);
                if (err2 == ErrorCodes.Good) vals = sv;
            }

            pvList.Add(new BacnetPropertyValue
            {
                property = pref,
                value    = vals ?? new List<BacnetValue>
                    {
                        new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR,
                            new BacnetError(BacnetErrorClasses.ERROR_CLASS_PROPERTY,
                                BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY))
                    },
            });
        }

        results.Add(new BacnetReadAccessResult(req.objectIdentifier, pvList));
    }

    sender.ReadPropertyMultipleResponse(adr, invokeId, default, results);
};

client.OnWritePropertyRequest += (sender, adr, invokeId, objectId, value, _) =>
{
    var err = storage.WriteProperty(objectId,
        (BacnetPropertyIds)value.property.propertyIdentifier,
        value.property.propertyArrayIndex, value.value, false);

    if (err != ErrorCodes.Good)
    {
        sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY,
            invokeId, BacnetErrorClasses.ERROR_CLASS_PROPERTY,
            BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED);
        return;
    }
    sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId);
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WRITE {objectId}  " +
        $"prop={(BacnetPropertyIds)value.property.propertyIdentifier}  " +
        $"val={value.value?.FirstOrDefault().Value}");
};

client.OnSubscribeCOV += (sender, adr, invokeId, processId, objectId,
                          cancel, confirmed, lifetime, _) =>
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

// NotifyCov – send current value to all active subscribers for an object.
// Called directly from SetReal / SetEnum / SetUint after each write.
void NotifyCov(BacnetObjectId objectId)
{
    string key = objectId.ToString();
    List<CovSub> subs;
    lock (covLock)
    {
        if (!covSubs.TryGetValue(key, out var raw))
            return;
        raw.RemoveAll(s => DateTime.UtcNow > s.ExpiresAt);
        subs = raw.ToList();
    }
    if (subs.Count == 0) return;

    // Use our runtime present-value table
    var pvVals = GetPresentValue(objectId);
    if (pvVals is null || pvVals.Count == 0) return;

    _ = storage.ReadProperty(objectId, BacnetPropertyIds.PROP_STATUS_FLAGS,
        uint.MaxValue, out IList<BacnetValue>? sfVals);

    var covValues = new List<BacnetPropertyValue>
    {
        new() { property = new BacnetPropertyReference(
                    (uint)BacnetPropertyIds.PROP_PRESENT_VALUE, uint.MaxValue),
                value = pvVals.ToList() },
        new() { property = new BacnetPropertyReference(
                    (uint)BacnetPropertyIds.PROP_STATUS_FLAGS, uint.MaxValue),
                value = sfVals ?? new List<BacnetValue>() },
    };

    foreach (var sub in subs)
    {
        uint remaining = (uint)Math.Max(0, (sub.ExpiresAt - DateTime.UtcNow).TotalSeconds);
        try
        {
            client.Notify(sub.Adr, sub.ProcessId, DEVICE_ID,
                objectId, remaining, sub.Confirmed, covValues);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] COV-> {objectId}  val={pvVals[0].Value}  to={sub.Adr}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] COV! NOTIFY FAILED {objectId}: {ex.Message}");
        }
    }
}

client.Start();
Console.WriteLine($"=== BACnet/IP Simulator  device={DEVICE_ID}  vendor=7 (Lalatec AG) ===");
Console.WriteLine($"Listening on UDP 0.0.0.0:{PORT}  |  objects={storage.Objects.Length}  |  svRoots={svRoots.Length}");
client.Iam(DEVICE_ID, BacnetSegmentations.SEGMENTATION_NONE, null, null);

// ── Simulation helpers ────────────────────────────────────────────────────────
var t0 = DateTime.UtcNow;

double Sine(double lo, double hi, double offset = 0)
{
    double e = (DateTime.UtcNow - t0).TotalSeconds + offset;
    return lo + (hi - lo) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * e / PERIOD));
}

void SetReal(BacnetObjectTypes t, uint i, double v)
{
    var bv = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, (float)Math.Round(v, 3));
    presentValues[new BacnetObjectId(t, i).ToString()] = bv;
    NotifyCov(new BacnetObjectId(t, i));
}

void SetEnum(BacnetObjectTypes t, uint i, uint v)
{
    var bv = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, v);
    presentValues[new BacnetObjectId(t, i).ToString()] = bv;
    NotifyCov(new BacnetObjectId(t, i));
}

void SetUint(BacnetObjectTypes t, uint i, uint v)
{
    var bv = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, v);
    presentValues[new BacnetObjectId(t, i).ToString()] = bv;
    NotifyCov(new BacnetObjectId(t, i));
}

// ── Update loop ───────────────────────────────────────────────────────────────
await Task.Run(async () =>
{
    double tick = 0;
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(5_000, cts.Token).ConfigureAwait(false);
        tick += 5;
        double phase = tick % PERIOD / PERIOD;

        SetReal(BacnetObjectTypes.OBJECT_ANALOG_INPUT,  1, Sine(19, 24,  0));
        SetReal(BacnetObjectTypes.OBJECT_ANALOG_INPUT,  2, Sine(-5, 30, 60));
        SetReal(BacnetObjectTypes.OBJECT_ANALOG_INPUT,  3, Sine(14, 20, 30));
        SetReal(BacnetObjectTypes.OBJECT_ANALOG_INPUT,  4, Sine(18, 23, 45));
        SetReal(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1, Sine(20, 90, 15));
        SetReal(BacnetObjectTypes.OBJECT_ANALOG_VALUE,  1, Sine(20, 22.5, 10));
        SetReal(BacnetObjectTypes.OBJECT_ANALOG_VALUE,  2, Sine(5, 80, 20));

        SetEnum(BacnetObjectTypes.OBJECT_BINARY_INPUT,  1, phase < 0.6 ? 1u : 0u);
        SetEnum(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1, phase < 0.8 ? 1u : 0u);
        SetEnum(BacnetObjectTypes.OBJECT_BINARY_VALUE,  1, phase > 0.7 ? 1u : 0u);

        SetUint(BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT,  1, (uint)(phase * 3) % 3 + 1);
        SetUint(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, 1, (uint)(phase * 4) % 4 + 1);
        SetUint(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE,  1, (uint)(tick / PERIOD) % 3 + 1);

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] " +
            $"AI1={Sine(19,24,0):F2}°C  AO1={Sine(20,90,15):F1}%  " +
            $"BI1={(phase < 0.6 ? "active" : "inact")}  MSI1={(uint)(phase*3)%3+1}");
    }
}, cts.Token);

Console.WriteLine("Shutting down.");
client.Dispose();

record CovSub(BacnetAddress Adr, uint ProcessId, bool Confirmed, DateTime ExpiresAt);
