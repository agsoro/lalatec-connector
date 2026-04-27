FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY BacnetSim.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish BacnetSim.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# BACnet/IP uses UDP 47808; host networking is set in docker-compose
EXPOSE 47808/udp
ENTRYPOINT ["dotnet", "BacnetSim.dll"]
