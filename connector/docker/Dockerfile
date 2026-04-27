# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore dependencies first (layer-cache friendly)
COPY Connector.csproj ./
RUN dotnet restore

# Copy source and publish a self-contained, trimmed binary
COPY . ./
RUN dotnet publish Connector.csproj \
        --configuration Release \
        --output /app/publish \
        --no-restore

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy published output from the build stage
COPY --from=build /app/publish .

# The connector reads its config from connector.json in the working directory.
# Mount your own file at runtime:
#   -v ./connector.json:/app/connector.json:ro
# or supply the path via CONNECTOR_CONFIG (handled below).
#
# Default: /app/connector.json   (shipped as a placeholder so the image starts)
COPY connector.example.json /app/connector.example.json

# Expose nothing – the connector is a pure outbound client (MQTT + HTTP to TB).
# BACnet/IP uses UDP on port 47808; if you need inbound BACnet responses expose:
# EXPOSE 47808/udp

ENTRYPOINT ["dotnet", "Connector.dll"]
