# FrontApi — REST API (ASP.NET Core)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY FrontApi.csproj .
RUN dotnet restore FrontApi.csproj

COPY . .
RUN dotnet publish FrontApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FrontApi.dll"]
