FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet build -c Release -o /app/build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS test
WORKDIR /app
COPY --from=build /app/build .
ENTRYPOINT ["dotnet", "test", "ThingsBoard.Tests.csproj", "--logger", "console", "--verbosity", "normal"]
