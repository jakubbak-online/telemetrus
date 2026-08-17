# AlertSetup — jednorazowy provisioner alertow InfluxDB (Notification Endpoint + Threshold Check
# + Notification Rule). To NIE jest dlugo dzialajaca usluga — kontener konczy dzialanie po
# wykonaniu Main(), dlatego (podobnie jak TelemetryWorker) final image bazuje na plain runtime,
# nie aspnet.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY AlertSetup.csproj .
RUN dotnet restore AlertSetup.csproj

COPY . .
RUN dotnet publish AlertSetup.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AlertSetup.dll"]
