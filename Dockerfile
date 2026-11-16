# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (better layer caching)
COPY ipx2mqtt.sln ./
COPY src/Ipx2Mqtt.Core/Ipx2Mqtt.Core.csproj src/Ipx2Mqtt.Core/
COPY src/Ipx2Mqtt.Ipx/Ipx2Mqtt.Ipx.csproj src/Ipx2Mqtt.Ipx/
COPY src/Ipx2Mqtt.Service/Ipx2Mqtt.Service.csproj src/Ipx2Mqtt.Service/
COPY tests/Ipx2Mqtt.Tests/Ipx2Mqtt.Tests.csproj tests/Ipx2Mqtt.Tests/
RUN dotnet restore src/Ipx2Mqtt.Service/Ipx2Mqtt.Service.csproj

COPY src/ src/
RUN dotnet publish src/Ipx2Mqtt.Service/Ipx2Mqtt.Service.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Extra settings (entity labels...): mount a folder holding ipx2mqtt.json on /config (see docker-compose.yml).
# The optional Push endpoint listens on Push__Port (default 8080) when Push__Enabled=true.
EXPOSE 8080
ENV DOTNET_EnableDiagnostics=0

# Runs as the non-root user provided by the base image
USER $APP_UID
ENTRYPOINT ["dotnet", "Ipx2Mqtt.Service.dll"]
