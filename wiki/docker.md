# lalatec-connector — Docker & Compose

> **Generated:** 2026-04-24  
> **Docker files:** `docker/`  
> **Compose entry point:** `docker/docker-compose.yml`

---

## 1. Quick start

```powershell
# 1. Copy and edit the connector config
copy docker\connector.compose.json docker\connector.compose.json   # already there
#    → open docker\connector.compose.json and fill in your fieldbus devices

# 2. Start the full stack (ThingsBoard + connector)
cd docker
docker compose up --build
```

ThingsBoard needs **~60–90 s** on the very first boot to initialise its database.  
The connector will automatically retry until ThingsBoard is ready.

| Service | URL / address |
|---|---|
| ThingsBoard UI | <http://localhost:8080> |
| MQTT broker | `localhost:1883` |

**Default logins:**

| Role | E-mail | Password |
|---|---|---|
| System admin | `sysadmin@thingsboard.org` | `sysadmin` |
| Tenant admin | `tenant@thingsboard.org` | `tenant` |
| Customer user | `user@thingsboard.org` | `customer` |

> **Note:** The connector authenticates as the *Tenant admin* to provision devices.

---

## 2. File overview

```
docker/
├── Dockerfile                # Multi-stage build: SDK → runtime image
├── .dockerignore             # Keeps build context lean
├── docker-compose.yml        # Full stack: ThingsBoard CE + lalatec-connector
└── connector.compose.json    # Connector config pre-wired to the compose network
```

---

## 3. Dockerfile

```
Build stage  (mcr.microsoft.com/dotnet/sdk:8.0)
  └─ dotnet restore
  └─ dotnet publish -c Release → /app/publish

Runtime stage  (mcr.microsoft.com/dotnet/aspnet:8.0)
  └─ COPY /app/publish
  └─ ENTRYPOINT ["dotnet", "Connector.dll"]
```

The runtime image is based on the **ASP.NET 8 runtime** (smaller than the full SDK).  
No ports are exposed — the connector is a pure *outbound* client (MQTT + HTTP to ThingsBoard).

> **BACnet note:** BACnet/IP uses UDP on port 47808.  
> If a BACnet device sends unsolicited responses the connector must receive,
> add `EXPOSE 47808/udp` and pass `--network host` (or configure a `ports:` mapping in compose).

### Build context

The build context is the **project root** (`..` relative to `docker/`).  
`docker/Dockerfile` is referenced as `dockerfile: docker/Dockerfile` in `docker-compose.yml`.

### Injecting the config at runtime

`connector.json` is **not baked into the image** — it is injected via a bind-mount:

```yaml
volumes:
  - ./connector.compose.json:/app/connector.json:ro
```

To use a different config, replace that bind-mount path or override it on the command line:

```powershell
docker run --rm `
  -v "$PWD\my-site.json:/app/connector.json:ro" `
  lalatec-connector
```

---

## 4. docker-compose.yml

### Services

| Service | Image | Ports |
|---|---|---|
| `thingsboard` | `thingsboard/tb-postgres:latest` | `8080` (UI), `1883` (MQTT), `7070` (Edge RPC), `5683/udp` (CoAP) |
| `lalatec-connector` | built from source | — (outbound only) |

### Start-up ordering

```
thingsboard starts
   │
   └─ health-check: GET /api/noauth/activate
         interval:     15 s
         timeout:      10 s
         retries:      12  (= up to 3 min wait)
         start_period: 90 s
         │
         └─ healthy → lalatec-connector starts
                          restart: on-failure (retries if TB not ready yet)
```

### Volumes

| Volume | Content |
|---|---|
| `tb-data` | ThingsBoard database + keystore (persists across restarts) |
| `tb-logs` | ThingsBoard log files |

Data is preserved across `docker compose down` / `docker compose up` cycles.  
To **reset** ThingsBoard to a clean state:

```powershell
docker compose down -v   # removes named volumes
```

### Environment variables (ThingsBoard)

| Variable | Value | Effect |
|---|---|---|
| `TB_QUEUE_TYPE` | `in-memory` | No external message broker needed (Kafka / RabbitMQ not required) |
| `TB_ADMIN_EMAIL` | `sysadmin@thingsboard.org` | Seeds the system admin account on first run |
| `TB_ADMIN_PASSWORD` | `sysadmin` | Seeds the system admin password on first run |

---

## 5. connector.compose.json

Pre-filled connector config that points to the `thingsboard` service on the compose network.

```jsonc
{
  "thingsboard": {
    "host":          "thingsboard",   // compose service name → resolves automatically
    "httpPort":      9090,            // TB internal HTTP port (mapped to 8080 externally)
    "mqttPort":      1883,
    "adminEmail":    "tenant@thingsboard.org",
    "adminPassword": "tenant"
  },
  …
}
```

> **Important:** The internal ThingsBoard port is **9090**, not 8080.  
> Port 8080 is the *host* port defined in `docker-compose.yml`.  
> Inside the compose network the connector reaches ThingsBoard directly on 9090.

### Adding your fieldbus devices

Edit `docker/connector.compose.json` and fill in `connections` and `devices`:

```jsonc
"connections": [
  {
    "id":   "my-modbus",
    "type": "modbus-tcp",
    "host": "192.168.1.100",   // host/IP of your PLC — must be reachable from the container
    "port": 502
  }
],
"devices": [
  {
    "name":         "My Energy Meter",
    "deviceType":   "janitza",
    "connectionId": "my-modbus",
    "slaveId":      1
  }
]
```

See [`connector.md`](connector.md) §4 for the full configuration reference.

> **Security:** `connector.compose.json` is listed in `.gitignore` — it may contain real credentials and must not be committed.

---

## 6. Building the image standalone

```powershell
# From the project root
docker build -f docker/Dockerfile -t lalatec-connector:latest .
```

Run standalone (supply your own config):

```powershell
docker run --rm `
  -v "$PWD\connector.json:/app/connector.json:ro" `
  lalatec-connector:latest
```

---

## 7. Useful commands

```powershell
# Start the full stack in the foreground (rebuild connector if source changed)
docker compose -f docker/docker-compose.yml up --build

# Start detached
docker compose -f docker/docker-compose.yml up -d --build

# Follow connector logs only
docker compose -f docker/docker-compose.yml logs -f lalatec-connector

# Follow ThingsBoard logs only
docker compose -f docker/docker-compose.yml logs -f thingsboard

# Stop without removing volumes (data preserved)
docker compose -f docker/docker-compose.yml down

# Stop and wipe all data (clean slate)
docker compose -f docker/docker-compose.yml down -v
```

> **Tip:** Run these from the **project root** using `-f docker/docker-compose.yml`, or `cd docker` first and omit `-f`.

---

## 8. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Connector exits immediately with `MQTT connect timeout` | ThingsBoard not ready yet | Wait for `thingsboard` to show `healthy`; the `restart: on-failure` policy will retry automatically |
| `Config file not found: /app/connector.json` | Bind-mount missing or wrong path | Ensure `docker/connector.compose.json` exists and the volume mount in compose is correct |
| ThingsBoard UI unreachable on `:8080` | Port conflict or first-boot still running | Wait 90 s; check `docker compose logs thingsboard` |
| BACnet device not reachable from container | Network isolation | BACnet/IP uses UDP broadcasts — use `network_mode: host` or a `macvlan` network for the connector service |
| Changes to source not picked up | Image cached | Run `docker compose up --build` to force a rebuild |
