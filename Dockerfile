FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY LabGuard.Server/LabGuard.Server.csproj LabGuard.Server/
RUN dotnet restore LabGuard.Server/LabGuard.Server.csproj
COPY LabGuard.Server/ LabGuard.Server/
RUN dotnet publish LabGuard.Server/LabGuard.Server.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# Ports come from appsettings.json (Server:Port / Server:ClientPort), so the image's default HTTP port is cleared.
# Everything the server keeps (database, client installers, PC key, login keys) lives in /data, a Docker volume.
ENV ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__AppDb="Data Source=/data/lykantra.db" \
    Server__PcChannelFile=/data/pc-channel.json \
    DataProtection__KeysFolder=/data/keys
# Created here so a new volume starts out owned by the non-root app user.
RUN mkdir -p /data && chown "$APP_UID" /data && chmod 700 /data
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "LabGuard.Server.dll"]
