# BewerbungsPilot – container image for the PUBLIC DEMO (see docs/deployment.md).
# Only src/BewerbungsPilot is copied in (see .dockerignore) – never storage/, never a database.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/BewerbungsPilot/BewerbungsPilot.csproj src/BewerbungsPilot/
RUN dotnet restore src/BewerbungsPilot/BewerbungsPilot.csproj
COPY src/BewerbungsPilot/ src/BewerbungsPilot/
RUN dotnet publish src/BewerbungsPilot/BewerbungsPilot.csproj --configuration Release --no-restore --output /app /p:UseAppHost=false
# Safety net: the image must not contain any data folder, database or document.
RUN if find /app -name 'storage*' -o -name '*.db*' -o -name '*.pdf' | grep -q .; then echo "Daten im Image gefunden" >&2; exit 1; fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY deploy/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 755 /usr/local/bin/docker-entrypoint.sh

# Defaults for the demo; fly.toml sets the host-specific values (AllowedHosts, proxy).
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Demo__Enabled=true \
    Storage__DataDirectory=/data/demo \
    DOTNET_gcServer=0

EXPOSE 8080
# Starts as root only to give the volume (/data) to the unprivileged "app" user, then drops to it.
ENTRYPOINT ["docker-entrypoint.sh"]
