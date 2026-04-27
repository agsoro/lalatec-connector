FROM lalatec-connector-lalatec-connector:latest
WORKDIR /app
ARG CACHEBUST=1
COPY publish/ .
ENTRYPOINT ["dotnet", "Connector.dll"]
