# NotificationWebApp — webhook receiver + SignalR hub + static UI (ASP.NET Core)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NotificationWebApp.csproj .
RUN dotnet restore NotificationWebApp.csproj

COPY . .
RUN dotnet publish NotificationWebApp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "NotificationWebApp.dll"]
