# lalatec-connector — Technical Documentation

> **Generated:** 2026-04-24  
> **Target:** `c:\src\thingsboard\lalatec-connector`  
> **Runtime:** .NET 8 · C# · `net8.0`

---

## 1. Overview

`lalatec-connector` is a self-contained .NET console service that bridges field-bus devices to [ThingsBoard](https://thingsboard.io/) using MQTT.

```
┌──────────────────────────────────────────────────────────────────┐
│  Field devices                                                   │
│                                                                  │
│   Janitza UMG ──┐                                               │
│   Glück CTRL ───┤── Modbus TCP ──────────────┐                  │
│   (any Modbus)  ┘                            │                  │
│                                              ▼                  │
│   BACnet/IP device ── BACnet/IP ──► BacnetReader               │
│                                              │                  │
│                                              ▼                  │
│                                       Program.cs                │
│                                    (polling loop)               │
│                                              │                  │
│                            ┌─────────────────┴───────────────┐  │
│                            │  MQTT (one client per device)   │  │
│                            │  v1/devices/me/telemetry        │  │
│                            │  v1/devices/me/attributes       │  │
│                            └─────────────────────────────────┘  │
│                                              │                  │
│                                       ThingsBoard               │
└──────────────────────────────────────────────────────────────────┘
```

**Key properties:**
- One MQTT connection per device (separate access token per device)
- Polling interval configurable globally; all devices polled in parallel
- BACnet devices publish both **timeseries** and **attributes**; Modbus devices publish timeseries only
- ThingsBoard devices are auto-provisioned on startup (idempotent)
- JWT token is refreshed automatically 5 minutes before expiry

---

## 2. Project structure

| File | Role |
|---|---|
| `Program.cs` | Entry point — config load, TB provisioning, polling loop, MQTT publish |
| `Config.cs` | JSON model — mirrors `connector.json` |
| `IDeviceReader.cs` | `IDeviceReader` interface + shared Modbus TCP helpers |
| `DeviceReaderFactory.cs` | Maps `deviceType` string → driver singleton |
| `JanitzaReader.cs` | Modbus TCP driver — Janitza energy meter |
| `GlueckReader.cs` | Modbus TCP driver — Glück controller |
| `BacnetReader.cs` | BACnet/IP driver — full object discovery + filter + poll |
| `ThingsBoardApi.cs` | ThingsBoard REST API — login, device provisioning, JWT refresh |
| `connector.json` | Runtime configuration file |
| `Connector.csproj` | Project / NuGet references |

---

## 3. NuGet dependencies

| Package | Version | Purpose |
|---|---|---|
| `NModbus` | 3.0.81 | Modbus TCP master |
| `MQTTnet` | 4.3.3.952 | MQTT client |
| `MQTTnet.Extensions.ManagedClient` | 4.3.3.952 | Auto-reconnect MQTT wrapper |
| `BACnet` | 3.0.2 | BACnet/IP stack (ela-compil / YABE) |

---

## 4. Configuration — `connector.json`

The file is loaded from the executable directory. Comments (`//`) are stripped before parsing.

### 4.1 Top-level structure

```json
{
  "thingsboard": { … },
  "polling":     { "intervalSeconds": 30 },
  "connections": [ … ],
  "devices":     [ … ]
}
```

### 4.2 `thingsboard`

```json
{
  "host":          "msr.lalatec-home.de",
  "httpPort":      8080,
  "mqttPort":      1883,
  "adminEmail":    "tenant@thingsboard.org",
  "adminPassword": "tenant"
}
```

The REST API is used only for provisioning (startup). Data is sent over MQTT.

### 4.3 `connections`

Each entry defines a reusable transport endpoint:

```json
{
  "id":   "bacnet-hvac",
  "type": "bacnet-ip",      // "modbus-tcp" | "bacnet-ip"
  "host": "192.168.1.200",
  "port": 47808
}
```

### 4.4 `devices` — common fields

```json
{
  "name":         "HVAC Controller AHU-01",   // used as TB device name
  "deviceType":   "bacnet",                   // see §4.5
  "connectionId": "bacnet-hvac"               // references a connection by id
}
```

The **access token** is derived deterministically from the device name:
```
"device_" + name.toLower().replace(' ','_').replace('/','_').replace('\\','_')
```

### 4.5 Device types

| `deviceType` | Driver | Extra fields |
|---|---|---|
| `janitza` | `JanitzaReader` | `slaveId` (byte) |
| `glueck` | `GlueckReader` | `slaveId` (byte) |
| `bacnet` | `BacnetReader` | `bacnetDeviceId` + `bacnet` block |

> **Note:** `DeviceReaderFactory` registers drivers under `"janitza"`, `"glueck"`, and `"bacnet-generic"`.
> The config example uses `"bacnet"` — make sure the key in the factory matches what you write in the JSON.

---

## 5. BACnet device configuration (`bacnet` block)

```jsonc
"bacnet": {
  "whoIsTimeoutMs": 2000,     // ms to wait for Who-Is / I-Am round-trip

  "filter": { … },            // which objects to keep (see §5.1)
  "properties": { … },        // which BACnet properties to read (see §5.2)
  "discovery": { … }          // when to (re-)run object discovery (see §5.3)
}
```

### 5.1 Filter block

All fields are **optional**. Omitting a field means "no restriction on that dimension."  
Filter steps are evaluated in order with short-circuit logic (network reads happen last to minimise traffic).

```jsonc
"filter": {
  // ── Object-type allowlist ─────────────────────────────────────────
  // Each entry can be any of:
  //   • Short alias     "AI" | "AO" | "AV" | "BI" | "BO" | "BV"
  //                     "MI" | "MO" | "MV"
  //   • Full enum name  "OBJECT_ANALOG_INPUT" (BacnetObjectTypes, case-insensitive)
  //   • Numeric type ID "128"  ← vendor/proprietary types ≥ 128
  // Null / empty array → all object types included.
  "objectTypes": ["AI", "AO", "AV", "BI", "BO", "BV", "MI", "MV",
                  "OBJECT_MULTI_STATE_OUTPUT", "128"],

  // ── Instance range ────────────────────────────────────────────────
  // Keep only objects whose instance number is in [min, max] inclusive.
  "instanceRange": { "min": 0, "max": 9999 },

  // ── Name filters (applied to PROP_OBJECT_NAME) ────────────────────
  // Include-regex: only objects whose name matches are kept.
  "namePattern": ".",

  // Exclude-regex: objects whose name matches are removed.
  // Applied after namePattern.
  "excludeNamePattern": "^(UNUSED|SPARE|TEST)",

  // ── Description filter (applied to PROP_DESCRIPTION) ─────────────
  // PROP_DESCRIPTION is only read when this field is non-null.
  // Include-regex: only objects whose description matches are kept.
  "descriptionPattern": "",       // "" matches everything

  // ── Safety cap ────────────────────────────────────────────────────
  // Stop after N objects have passed all filters. 0 / null = unlimited.
  "maxObjects": 0
}
```

**Filter evaluation order (cheapest first):**

| Step | Check | Network I/O |
|---|---|---|
| 1 | Skip `OBJECT_DEVICE` itself | none |
| 2 | `objectTypes` allowlist | none |
| 3 | `instanceRange` | none |
| 4 | `namePattern` / `excludeNamePattern` | reads `PROP_OBJECT_NAME` |
| 5 | `descriptionPattern` | reads `PROP_DESCRIPTION` |
| 6 | `maxObjects` cap | none |

### 5.2 Properties block

Controls which BACnet properties are read from each discovered object.

```jsonc
"properties": {
  // Published to v1/devices/me/telemetry on every poll (timestamped).
  "telemetry": ["PROP_PRESENT_VALUE"],

  // Published to v1/devices/me/attributes once on startup
  // and again after each rediscovery.
  "attributes": [
    "PROP_OBJECT_NAME",
    "PROP_DESCRIPTION",
    "PROP_UNITS",
    "PROP_STATUS_FLAGS"
  ]
}
```

Property names are `BacnetPropertyIds` enum names (case-insensitive).

### 5.3 Discovery block

```jsonc
"discovery": {
  "onStartup": true,              // run discovery on first poll
  "refreshIntervalMinutes": 60    // re-discover every N minutes (0 = never)
}
```

Discovery reads `PROP_OBJECT_LIST` from the device object.  
If the bulk read fails (some devices segment the list), the driver falls back to index-by-index reading
(BACnet spec §12.11 — array index 0 = count, 1…N = entries).

---

## 6. BACnet driver internals (`BacnetReader.cs`)

### 6.1 Data flow per poll cycle

```
ReadFull(conn, device)
  │
  ├─ OpenClient()               → BacnetClient (UDP, exclusive port)
  ├─ ResolveAddress()           → BacnetAddress
  │    ├─ construct direct IP address from config (unicast)
  │    └─ broadcast WhoIs → wait for I-Am (confirms liveness)
  │         └─ fall back to direct address if no I-Am arrives
  │
  ├─ [if discovery needed]
  │    ├─ DiscoverObjects()     → List<BacnetObjectId>
  │    │    ├─ ReadProperty(DEVICE, PROP_OBJECT_LIST)   [bulk]
  │    │    └─ fallback: ReadProperty by index 1…N
  │    │
  │    └─ ApplyFilter()         → List<BacnetObjectInfo>
  │         ├─ type allowlist   (no I/O)
  │         ├─ instance range   (no I/O)
  │         ├─ ReadObjectName() → PROP_OBJECT_NAME  (if name/desc filter active)
  │         └─ ReadDescription()→ PROP_DESCRIPTION  (if descriptionPattern active)
  │
  └─ foreach cached BacnetObjectInfo
       └─ ReadObjectProperties()
            ├─ ReadPropertyMultiple (all props in one request)
            └─ fallback: ReadProperty one by one
```

### 6.2 ThingsBoard key naming

Each discovered object gets a **key prefix**:

```
{shortType}_{instance}_{sanitisedName}
```

Examples:

| Object | Key prefix |
|---|---|
| `OBJECT_ANALOG_INPUT:0 "Room Temp"` | `ai_0_room_temp` |
| `OBJECT_BINARY_VALUE:5 "Fan On/Off"` | `bv_5_fan_on_off` |
| `OBJECT_MULTI_STATE_VALUE:12 "Mode"` | `mv_12_mode` |

Short type names:

| Enum | Alias |
|---|---|
| `OBJECT_ANALOG_INPUT` | `ai` |
| `OBJECT_ANALOG_OUTPUT` | `ao` |
| `OBJECT_ANALOG_VALUE` | `av` |
| `OBJECT_BINARY_INPUT` | `bi` |
| `OBJECT_BINARY_OUTPUT` | `bo` |
| `OBJECT_BINARY_VALUE` | `bv` |
| `OBJECT_MULTI_STATE_INPUT` | `mi` |
| `OBJECT_MULTI_STATE_OUTPUT` | `mo` |
| `OBJECT_MULTI_STATE_VALUE` | `mv` |
| `OBJECT_DEVICE` | `dev` |
| anything else | lowercased enum name without `OBJECT_` prefix |

The full key for a **telemetry** or **attribute** property: `{prefix}_{propSuffix}`

| `BacnetPropertyIds` | Suffix |
|---|---|
| `PROP_PRESENT_VALUE` | `value` |
| `PROP_OBJECT_NAME` | `name` |
| `PROP_DESCRIPTION` | `desc` |
| `PROP_UNITS` | `units` |
| `PROP_STATUS_FLAGS` | `status` |
| `PROP_OUT_OF_SERVICE` | `oos` |
| anything else | lowercased enum name without `PROP_` prefix |

### 6.3 Per-device discovery state

`BacnetReader` keeps a `Dictionary<string, DiscoveryState>` keyed by device name (thread-safe, locked on a private object).

| Field | Meaning |
|---|---|
| `DiscoveryDone` | Whether initial discovery has run |
| `LastDiscovery` | `DateTime.UtcNow` at last discovery |
| `CachedObjects` | `List<BacnetObjectInfo>` after filter |
| `AttributesSent` | `true` after first attribute publish; reset to `false` on rediscovery |

### 6.4 `BacnetObjectInfo` record

```csharp
record BacnetObjectInfo(
    BacnetObjectId ObjectId,
    string         ObjectName,       // from PROP_OBJECT_NAME
    string         Description = ""  // from PROP_DESCRIPTION (when filter active)
)
```

---

## 7. Modbus drivers

Both Modbus drivers use the shared `ModbusHelper` (in `IDeviceReader.cs`).

### `JanitzaReader` — Janitza UMG 604/605-PRO

| Register (0-based) | Key | Unit | Conversion |
|---|---|---|---|
| 19020 | `power_kw` | kW | raw W ÷ 1000 |
| 19060 | `import_kwh` | kWh | raw Wh ÷ 1000 |
| 19062 | `export_kwh` | kWh | raw Wh ÷ 1000 |

Registers are big-endian float32 (2 × 16-bit).

> **Warning:** Register addresses vary by Janitza model.  
> Verify against the [official Modbus address list](https://www.janitza.com/en/downloads/modbus-address-list) for your exact device.

### `GlueckReader` — Glück controller

| Register (0-based) | Key | Unit |
|---|---|---|
| 0x1230 (4656) | `active_allowed_power_pct` | % |

---

## 8. ThingsBoard publish topics

| Topic | Content | When |
|---|---|---|
| `v1/devices/me/telemetry` | `{ "ts": <ms>, "values": { … } }` | Every poll cycle |
| `v1/devices/me/attributes` | `{ "key": "value", … }` | Once after discovery (and after rediscovery) |

MQTT QoS: **At least once (QoS 1)**.  
The managed client queues messages during disconnects and delivers them after reconnect.

---

## 9. Startup sequence

```
1. Load connector.json
2. Validate all devices reference a known connectionId
3. Login to ThingsBoard REST API → obtain JWT
4. EnsureDeviceAsync() for each device (create if missing, set access token)
5. Create one ManagedMqttClient per device, connect, wait up to 8 s
6. Enter polling loop:
     └─ every intervalSeconds:
          ├─ PollAndPublishAsync() for all devices in parallel (Task.WhenAll)
          └─ RefreshIfNeededAsync() (refreshes JWT if < 5 min remaining)
7. Ctrl+C → graceful shutdown (stop all MQTT clients)
```

---

## 10. Adding a new device driver

1. Create `MyDeviceReader.cs` implementing `IDeviceReader`:
   ```csharp
   class MyDeviceReader : IDeviceReader
   {
       public string DriverName => "MyDriver";
       public Telemetry Read(ConnectionConfig conn, DeviceConfig device) { … }
   }
   ```
2. Register it in `DeviceReaderFactory.cs`:
   ```csharp
   ["mydevice"] = new MyDeviceReader(),
   ```
3. Add a device entry in `connector.json`:
   ```json
   { "name": "My Device", "deviceType": "mydevice", "connectionId": "…" }
   ```

If the driver also returns **attributes**, implement a `ReadFull()` method returning a result type
with both `Telemetry` and `Attributes` bags, and handle it in `Program.PollAndPublishAsync()`
the same way `BacnetReader` does.

---

## 11. Running the connector

```powershell
# Development
cd c:\src\thingsboard\lalatec-connector
dotnet run

# Production build
dotnet publish -c Release -o ./publish
./publish/Connector.exe
```

`connector.json` must be present next to the executable
(copied automatically by the build via `CopyToOutputDirectory: PreserveNewest`).

---

## 12. Write-back: ThingsBoard → field device

The connector is bidirectional. In addition to polling devices and pushing values **up** to ThingsBoard, it can receive commands from ThingsBoard and push values **down** to the field device.

### 12.1 Architecture

```
ThingsBoard (UI / Rule Chain)
      │
      │  MQTT – existing per-device connection
      │
      ├─ v1/devices/me/attributes        (shared attribute update)
      └─ v1/devices/me/rpc/request/+    (server-side RPC call)
              │
       Connector (HandleWriteBackAsync)
              │
       IDeviceWriter.Write(key, value)
              │
       ├─ GlueckReader  →  ModbusHelper.WriteRegister  →  Modbus TCP
       └─ BacnetReader  →  BacnetClient.WritePropertyRequest  →  BACnet/IP
```

Write-back is **opt-in per device** via the `writeback` config block. Devices without `writeback` are unaffected.

### 12.2 Trigger mechanisms

| Mechanism | MQTT topic (subscribe) | TB action |
|---|---|---|
| **Shared Attribute** | `v1/devices/me/attributes` | Operator edits a *Shared Attribute* in the TB device page |
| **Server-side RPC** | `v1/devices/me/rpc/request/+` | Rule Chain or dashboard widget calls `sendRpc` |

Both use the same existing MQTT connection (one per device). The connector adds subscriptions on startup for devices with `writeback` enabled.

### 12.3 Config: `writeback` block

```jsonc
"writeback": {
  "sharedAttributes": true,   // react to shared-attribute updates
  "rpc":              true    // react to server-side RPC calls
}
```

Both flags are `false` by default — omitting `writeback` entirely is equivalent to `false/false`.

### 12.4 Modbus write-back (`writableRegisters`)

The driver reads the register map from config. Add a `writableRegisters` array to any Modbus device:

```jsonc
"writableRegisters": [
  { "key": "active_allowed_power_pct", "address": "0x1230", "type": "float32-be" },
  { "key": "setpoint_deg_c",           "address": "1024",   "type": "int16"      }
]
```

| Field | Description |
|---|---|
| `key` | ThingsBoard attribute/telemetry key (case-insensitive match) |
| `address` | 0-based register address — decimal (`"4656"`) or hex (`"0x1230"`) |
| `type` | Data type — see table below |

**Supported `type` values:**

| `type` | Modbus function | Register count | Notes |
|---|---|---|---|
| `float32-be` | `WriteMultipleRegisters` | 2 | IEEE 754, big-endian word order (default) |
| `int32-be` | `WriteMultipleRegisters` | 2 | Signed 32-bit, big-endian |
| `uint32-be` | `WriteMultipleRegisters` | 2 | Unsigned 32-bit, big-endian |
| `int16` | `WriteSingleRegister` | 1 | Signed 16-bit |
| `uint16` | `WriteSingleRegister` | 1 | Unsigned 16-bit |
| `coil` | `WriteSingleCoil` | — | value ≠ 0 → true |

> **Note:** `JanitzaReader` is read-only and does **not** implement write-back.

### 12.5 BACnet write-back

#### Key format

BACnet write-back targets the same key format that the connector publishes as telemetry:

```
{typeAlias}_{instance}_{sanitisedName}_{propSuffix}
```

The writer extracts only the first two tokens (`typeAlias` + `instance`) to identify the target object. The rest of the key is ignored.

| Example key | Resolves to |
|---|---|
| `ao_3_supply_sp_value` | `OBJECT_ANALOG_OUTPUT:3` |
| `av_7_setpoint_value` | `OBJECT_ANALOG_VALUE:7` |
| `bv_12_fan_enable_value` | `OBJECT_BINARY_VALUE:12` |

Only short type aliases (ao, av, bo, bv, mo, mv) are accepted for writes.

#### Commandability detection

During object discovery, the connector probes `PROP_PRIORITY_ARRAY` for every output/value type object. If the device responds, the object is flagged `Commandable = true` and stored in the discovery cache.

| Object types probed | Commandable? |
|---|---|
| AO, AV, BO, BV, MO, MV | Yes (if `PROP_PRIORITY_ARRAY` present) |
| AI, BI, MI | Never probed — always read-only |

This matches the Siemens Desigo PXC/CC convention: operator-settable setpoints are AV/AO objects with priority arrays; sensor readings are AI objects without.

A write to a non-commandable object is rejected with a clear error message. The field device is never contacted.

#### Priority

`WritePropertyRequest` is called **without a priority** (`priority = null`). The device uses its own default (no priority array slot reserved). This avoids permanently seizing a high-priority slot that could override the automation program.

### 12.6 RPC payload convention

```json
{
  "method": "setValue",
  "params": {
    "key":   "ao_3_supply_sp_value",
    "value": 21.0
  }
}
```

The connector replies on `v1/devices/me/rpc/response/{requestId}`:

```json
{ "success": true }
```
or on error:
```json
{ "error": "Object 'ai_0_…' is not commandable (no PROP_PRIORITY_ARRAY)." }
```

To call from ThingsBoard Rule Chain or REST API:
```
POST /api/plugins/rpc/twoway/{deviceId}
Body: { "method": "setValue", "params": { "key": "…", "value": … }, "timeout": 5000 }
```

### 12.7 Console log output

| Log line | Meaning |
|---|---|
| `[WB] Glueck … → subscribed to shared-attribute updates.` | Startup: subscription active |
| `[WB←TB] Glueck …  attr  active_allowed_power_pct = 42.5` | Attribute update received + written |
| `[WB←TB] HVAC …    rpc   ao_3_… = 21.0  (reqId=42)` | RPC received + written |
| `[WB←TB] ERROR …: InvalidOperationException: not commandable …` | Write rejected |

---

## 13. Desigo hierarchy → ThingsBoard Assets

Siemens Desigo PXC/PXC5/PXC7 controllers expose their engineering tree via **BACnet Structured View objects** (`OBJECT_STRUCTURED_VIEW`, type 29).  
The connector can walk this tree and materialise it as a **ThingsBoard Asset hierarchy** with `"Contains"` relations.

### 13.1 How it works

```
DEVICE.PROP_STRUCTURED_OBJECT_LIST (property 209)
   └─ lists top-level Structured View IDs
        │
        └─ OBJECT_STRUCTURED_VIEW.PROP_SUBORDINATE_LIST (property 355)
             ├─ child Structured View  → recurse
             └─ child data-point (AI, AV, …) → leaf asset
```

Properties read per node:

| Property | Usage |
|---|---|
| `PROP_OBJECT_NAME` | Full dot-path (e.g. `Building.Floor2.Room201.TempSP`); last segment = Asset name |
| `PROP_DESCRIPTION` | Human-readable label → `description` attribute |
| `PROP_PROFILE_NAME` | Siemens point-type string → `profile_name` attribute |
| `PROP_UNITS` | Engineering unit (for data-point leaves) → `units` attribute |

### 13.2 ThingsBoard model produced

```
[Asset]  Building                         type = "BACnet Node"
  └─ Contains → [Asset]  Floor2
       └─ Contains → [Asset]  Room201
            └─ Contains → [Asset]  TempSP   (leaf data-point)
                 └─ Contains → [Device]  HVAC Controller AHU-01
```

Every Asset gets the following **SERVER_SCOPE attributes**:

| Attribute | Value |
|---|---|
| `bacnet_path` | Full dot-path, e.g. `Building.Floor2.Room201.TempSP` |
| `bacnet_type` | Short type alias (`ai`, `av`, `view`, …) |
| `bacnet_instance` | Numeric instance (leaf nodes only) |
| `description` | From `PROP_DESCRIPTION` |
| `profile_name` | From `PROP_PROFILE_NAME` |
| `units` | From `PROP_UNITS` (leaf nodes only, when present) |

### 13.3 Config: `hierarchy` block

```jsonc
"hierarchy": {
  "enabled":   true,        // false (or omit block) to disable for non-Desigo devices
  "assetType": "BACnet Node"  // TB asset type string; choose to match your naming convention
}
```

Add this block inside the device's `bacnet` config block (see `connector.example.json`).

### 13.4 Startup behaviour

```
Startup
  │
  ├─ ThingsBoard provisioning (devices)
  ├─ MQTT clients connected
  │
  └─ [background Task per device, hierarchy.enabled = true]
        │
        ├─ Wait for first poll cycle → BACnet discovery runs → Structured Views walked
        └─ DesigoProvisioner.ProvisionAsync()
              ├─ EnsureAssetAsync() for each node (idempotent)
              ├─ SetAssetAttributesAsync() with description / path / type
              └─ EnsureRelationAsync() for parent→child and leaf→device
```

The provisioner runs **once** after startup.  Provisioning is fully **idempotent** — re-running the connector will not create duplicate assets or relations.

### 13.5 Files involved

| File | Role |
|---|---|
| `BacnetHierarchy.cs` | Walks Structured View tree → `DesigoTree` |
| `DesigoProvisioner.cs` | Materialises `DesigoTree` into TB Assets + Relations |
| `ThingsBoardApi.cs` | `EnsureAssetAsync`, `EnsureRelationAsync`, `SetAssetAttributesAsync` |
| `BacnetReader.cs` | Stores tree in `DiscoveryState.Tree`; exposes `GetDiscoveredTree()` |
| `Program.cs` | Launches background provisioning job |

### 13.6 Graceful degradation

If the device does not expose any Structured View objects (e.g. generic BACnet devices, Modbus, etc.):

- `PROP_STRUCTURED_OBJECT_LIST` returns empty → `DesigoTree.Roots` is empty.
- The provisioner logs `[Hierarchy] No Structured View roots – nothing to provision.` and exits cleanly.
- Normal polling continues unaffected.
- Leave `hierarchy.enabled` as `false` (or omit the block) for non-Desigo devices.

