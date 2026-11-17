# syntax=docker/dockerfile:1

# ---- web interface (Vue + Vuetify) ----
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# vite.config.js writes to ../src/Ipx2Mqtt.Service/wwwroot
RUN npm run build

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
COPY --from=web /src/Ipx2Mqtt.Service/wwwroot src/Ipx2Mqtt.Service/wwwroot
RUN dotnet publish src/Ipx2Mqtt.Service/Ipx2Mqtt.Service.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Configuration: mount a folder holding config.yml and entities.yml on /config (see docker-compose.yml).
# It must be writable by the container user for the web interface to save. The web interface listens on
# Web__Port (default 8080); the optional Push endpoint on Push__Port when Push__Enabled=true.
EXPOSE 8080
ENV DOTNET_EnableDiagnostics=0

# Runs as the non-root user provided by the base image
USER $APP_UID
ENTRYPOINT ["dotnet", "Ipx2Mqtt.Service.dll"]
