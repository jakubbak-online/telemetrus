# TelemetryWorker — background worker (no HTTP listener), so the final
# image uses the plain runtime base instead of aspnet.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY TelemetryWorker.csproj .
RUN dotnet restore TelemetryWorker.csproj

COPY . .
RUN dotnet publish TelemetryWorker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TelemetryWorker.dll"]
