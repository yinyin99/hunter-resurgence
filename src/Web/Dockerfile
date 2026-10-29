# syntax=docker/dockerfile:1
# Build context: the repository root (docker build -f src/Web/Dockerfile .), for global.json, src/Ui and src/Services.
# the SDK of global.json, as in CI and for the committed lock files: bump all three together
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY global.json ./
# all sources before the restore: the SDK adds an implicit package reference only when it sees the project's .razor files
COPY src ./src
# portable (no runtime identifier), so the committed lock files apply as they are
RUN dotnet restore src/Web/Resurgence.Web.csproj --locked-mode
RUN dotnet publish src/Web/Resurgence.Web.csproj -c Release --no-restore -o /out

# the runtime that SDK ships (10.0.403 = 10.0.14); the standard image listens on 8080 (ASPNETCORE_HTTP_PORTS) and has the non-root user $APP_UID
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
# the account mock lists cultures, regions and time zones: fail the build if ICU or tzdata is missing
RUN ls /usr/share/zoneinfo/Europe/Paris /usr/lib/*/libicuuc.so.* > /dev/null
WORKDIR /app
COPY --from=build /out ./
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Resurgence.Web.dll"]
