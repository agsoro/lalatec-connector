# Deziko Connector

The Deziko Connector is a high-performance gateway designed to bridge industrial building automation protocols with the ThingsBoard IoT platform. It enables seamless telemetry ingestion, attribute synchronization, and alarm management for BACnet/IP and Modbus TCP devices.

## Key Features

- **BACnet/IP Integration**: 
  - Automated object discovery and filtering.
  - **Change-of-Value (COV)** support for low-latency updates with automated polling fallback.
  - Automatic **Hierarchy Extraction**: Walks Structured View trees to build corresponding Asset structures in ThingsBoard.
  - Intelligent Alarm Routing: Maps BACnet faults directly to the relevant assets.
- **Modbus TCP Integration**: 
  - Efficient polling for energy meters and power quality analyzers.
  - Support for multi-register encoding (Float32, etc.).
- **Automated Provisioning**: Dynamically creates Devices, Assets, and Relations in ThingsBoard based on the discovered field-bus structure.
- **Resilience**: Integrated background jobs for hierarchy synchronization and trend log backfilling.

## Project Structure

- `/connector`: The main .NET core application.
- `/testing/bacnet-sim`: A data-driven BACnet/IP simulator for end-to-end testing.
- `/testing/modbus-sim`: A Modbus TCP simulator mimicking energy meter behavior.
- `/tools/bacnet-dump`: A diagnostic CLI tool for inspecting field devices and generating simulator data.

## Getting Started

### Prerequisites
- Docker and Docker Compose
- .NET 8.0 SDK (for local development)

### Quick Start (Docker)
To spin up the entire stack (ThingsBoard, Connector, and Simulators):

```powershell
docker compose up --build -d
```

ThingsBoard will be available at `http://localhost:8080`. The connector will wait for ThingsBoard to be healthy before it starts provisioning assets.

### Configuration
The connector is configured via `connector.json`. This file defines:
- ThingsBoard connection credentials.
- BACnet device discovery filters and COV settings.
- Modbus register mappings and polling intervals.

## Usage

### Building locally
```powershell
dotnet build connector/Connector.csproj
```

### Discovery Tool
Use the `bacnet-dump` tool to inspect a device and generate configuration snippets:
```powershell
dotnet run --project tools/bacnet-dump -- <ip-address> <device-id> --json
```

## License
Deziko Internal Project.
