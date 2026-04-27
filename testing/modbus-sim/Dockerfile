FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ModbusSim.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish ModbusSim.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 502
ENTRYPOINT ["dotnet", "ModbusSim.dll"]
