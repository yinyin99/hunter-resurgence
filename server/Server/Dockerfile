# syntax=docker/dockerfile:1
# Build context: the repository root (docker build -f server/Server/Dockerfile .), for global.json.
# Same SDK and runtime tags as src/Web/Dockerfile: bump all of them together (docs/deploy.md).
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY global.json ./
COPY server/Server ./server/Server
COPY server/facts ./server/facts
COPY src/Restore ./src/Restore
# portable (no runtime identifier), so the committed lock file applies as it is
RUN dotnet restore server/Server/Server.csproj --locked-mode
RUN dotnet publish server/Server/Server.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
COPY --from=build /out ./
# data_dir of settings.json is /data and the backup sidecar writes /backups: volumes in deploy/compose.yml; a new named volume takes this folder's owner, so the app can write it
RUN mkdir /data /backups && chown $APP_UID /data /backups
# the settings file is mounted here (SERVER_SETTINGS); its relative paths are relative to it. Plain HTTP listener on 8080, the proxy's side
ENV SERVER_SETTINGS=/srv/settings.json
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "server.dll"]
