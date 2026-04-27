# Testing Guide

This document explains the end-to-end test environment included in the `testing/` folder.
One `docker compose up --build` from the repo root starts the entire stack.

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  Docker Compose stack                                    │
│                                                          │
│  ┌──────────────┐    MQTT/HTTP    ┌──────────────────┐  │
│  │  ThingsBoard │◄───────────────│lalatec-connector │  │
│  │  :8080 (UI)  │                │  (C# .NET 8)     │  │
│  │  :1883 (MQTT)│                └──────┬───────────┘  │
│  └──────────────┘                       │               │
│                                  Modbus │  BACnet/IP    │
│                                  TCP    │  UDP          │
│                           ┌────────────┘      │         │
│                           ▼                   ▼         │
│                  ┌─────────────┐   ┌──────────────────┐ │
│                  │ modbus-sim  │   │   bacnet-sim     │ │
│                  │ (C# .NET 8) │   │  (C# .NET 8)    │ │
│                  │  :502 TCP   │   │  :47808 UDP      │ │
│                  └─────────────┘   └──────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

---

## Quick Start

```powershell
# From the repo root
docker compose up --build
```

ThingsBoard takes **~90 seconds** on first boot to initialise its database.
The connector retries automatically until TB is healthy.

Once running, open **http://localhost:8080** and sign in:

| Role          | Email                        | Password   | Can see devices? |
|---------------|------------------------------|------------|------------------|
| **Tenant admin**  | `tenant@thingsboard.org` | `tenant`   | ✅ **Yes — use this** |
| System admin  | `sysadmin@thingsboard.org`   | `sysadmin` | ❌ No — wrong scope |

> [!IMPORTANT]
> The connector provisions all devices under the **tenant** account.
> Log in as **`tenant@thingsboard.org / tenant`** to see Devices, Assets, and Telemetry.
> The system-admin account manages tenants and billing — it cannot see tenant-scoped entities.

---

## Test Devices

### `Test Janitza Meter` — Modbus TCP

| Property        | Value                         |
|-----------------|-------------------------------|
| Connection      | `modbus-sim:502`              |
| Slave ID        | `1`                           |
| Device type     | `janitza`                     |
| Poll interval   | 30 s                          |

**Simulated telemetry:**

| ThingsBoard key | Register | Behaviour                                    |
|-----------------|----------|----------------------------------------------|
| `power_kw`      | 19020    | Sine wave 2–8 kW, 5-minute period            |
| `import_kwh`    | 19060    | Accumulates from random start (1–500 MWh)    |
| `export_kwh`    | 19062    | Always `0`                                   |

Values use float32 big-endian encoding, matching the real Janitza UMG-604/605-PRO register map.

---

### `Test BACnet AHU` — BACnet/IP with COV

| Property        | Value                         |
|-----------------|-------------------------------|
| Connection      | `172.17.0.1:47808`            |
| Device ID       | `1001`                        |
| Vendor          | `7` (Siemens AG)              |
| Model           | `PXC001-E.D`                  |
| COV             | Enabled                       |

**Simulated objects (Desigo naming convention):**

| Object          | Name                  | Description                          | Behaviour                     |
|-----------------|-----------------------|--------------------------------------|-------------------------------|
| `AI:1`          | `AI1.RoomTemp`        | Room temperature (°C)                | Sine 19–24 °C                |
| `AI:2`          | `AI2.OutdoorTemp`     | Outdoor air temperature (°C)         | Sine −5–30 °C (slow cycle)   |
| `AI:3`          | `AI3.SupplyAirTemp`   | Supply air temperature (°C)          | Sine 14–20 °C                |
| `AI:4`          | `AI4.ReturnAirTemp`   | Return air temperature (°C)          | Sine 18–23 °C                |
| `AO:1`          | `AO1.SupplyFanSpeed`  | Fan speed setpoint (%) — commandable | Sine 20–90 %                 |
| `AV:1`          | `AV1.RoomTempSetpoint`| Room setpoint (°C) — commandable     | Sine 20–22.5 °C              |
| `AV:2`          | `AV2.CoolingLoad`     | Cooling load (%)                     | Sine 5–80 %                  |
| `BI:1`          | `BI1.Occupancy`       | PIR occupancy                        | Active 60% of cycle          |
| `BI:2`          | `BI2.FireAlarm`       | Fire alarm relay                     | Always `inactive`             |
| `BO:1`          | `BO1.FanEnable`       | Fan enable — commandable             | Active 80% of cycle          |
| `BV:1`          | `BV1.NightSetback`    | Night setback — commandable          | Active last 30% of cycle     |
| `MSI:1`         | `MSI1.OpMode`         | Operating mode (1=Off 2=Auto 3=Man)  | Steps 1→2→3 per cycle        |
| `MSO:1`         | `MSO1.DamperPos`      | Damper position — commandable        | Steps 1→2→3→4 per half-cycle |
| `MSV:1`         | `MSV1.SeasonMode`     | Season mode (1=Sum 2=Win 3=Trans)    | Steps every 5 min            |

Values update every **5 seconds**. COV notifications are sent to all active
subscribers on each update cycle.

---

## Verifying the Test Run

### 1. Connector logs

```
[MQTT ✓] Test Janitza Meter
[MQTT ✓] Test BACnet AHU
...
[10:15:32] [Janitza   ] Test Janitza Meter          power_kw=4.821  import_kwh=182345.3
[10:15:37] [BACnet    ] Test BACnet AHU             [COV] attrs=5 fallback-tel=0
```

### 2. ThingsBoard UI

> [!IMPORTANT]
> You must be logged in as **`tenant@thingsboard.org / tenant`**.
> Devices are provisioned in the tenant scope — sysadmin cannot see them.

1. **Devices** → confirm `Test Janitza Meter` and `Test BACnet AHU` are provisioned.
2. Click a device → **Latest Telemetry** → values updating every 30 s.
3. **Attributes** tab on the BACnet device → `AI1.RoomTemp_units`, `AI1.RoomTemp_description`, etc.

### 3. Debug the Modbus simulator directly

The Modbus sim exposes port 502 on the host, so you can connect with any Modbus client:

```powershell
# Quick sanity check with mbpoll (if installed)
mbpoll -t 4:float -r 19021 -c 2 -1 localhost
```

Or use **Modscan**, **QModMaster**, or **Simply Modbus TCP** to read registers 19020–19063.

---

## Networking Notes

### Modbus
The `modbus-sim` container is on the standard Compose network.
The hostname `modbus-sim` resolves automatically to the connector.

### BACnet
BACnet/IP relies on UDP broadcasts which do not cross Docker network bridges.
The `bacnet-sim` container therefore uses `network_mode: host`, meaning it binds
directly to the host's network interface.

The connector reaches it via the Docker gateway IP (`172.17.0.1` on Linux).
On Windows/macOS Docker Desktop this is handled by `host.docker.internal`.

> [!NOTE]
> If you are running Docker Desktop on Windows/macOS and the BACnet device is not
> discovered, update the `bacnet-test` connection `host` in
> `connector/docker/connector.compose.json` from `172.17.0.1` to
> `host.docker.internal`.

---

## Standalone connector-only stack

The `connector/docker/docker-compose.connector-only.yml` starts only
ThingsBoard and the connector — no simulators. Point it at real hardware by
editing `connector/docker/connector.compose.json`.

```powershell
cd connector\docker
docker compose -f docker-compose.connector-only.yml up --build
```

---

## Adding More Test Devices

### Modbus
Edit `testing/modbus-sim/Program.cs`:
- Add register addresses and a new sine/noise generator.
- Expose a new slave ID by calling `factory.CreateSlave(slaveId)`.

### BACnet
Edit `testing/bacnet-sim/device.xml`:
- Add `<Object>` blocks with any BACnet object type.
- Add the new object ID to `PROP_OBJECT_LIST` in the device object.
- Update `Program.cs` simulation loop to drive `PROP_PRESENT_VALUE` on the new object.
