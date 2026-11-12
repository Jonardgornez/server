# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY LabGuard.Server/LabGuard.Server.csproj LabGuard.Server/

RUN dotnet restore LabGuard.Server/LabGuard.Server.csproj

COPY LabGuard.Server/ LabGuard.Server/

RUN dotnet publish LabGuard.Server/LabGuard.Server.csproj \
    -c Release \
    -o /app \
    --no-restore \
    -p:UseAppHost=false


# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__AppDb="Data Source=/data/lykantra.db" \
    Server__PcChannelFile=/data/pc-channel.json \
    DataProtection__KeysFolder=/data/keys

# Persistent data directory
RUN mkdir -p /data \
    && chown "$APP_UID" /data \
    && chmod 700 /data

COPY --from=build /app .

# Run as non-root user
USER $APP_UID

EXPOSE 5000
EXPOSE 5001

ENTRYPOINT ["dotnet", "LabGuard.Server.dll"]