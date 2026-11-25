# FileServe as a container: the headless server (heads/ShinyFileServe.Headless) with the Blazor portal
# embedded in it. No MAUI, no tray - the portal is the whole interface, the same as on the desktop.
#
#   docker build -t fileserve .
#   docker run -d -p 8080:8080 -v ./data:/data -v /path/to/files:/shares fileserve
#
# Plain Dockerfile syntax only - no BuildKit extras - so it builds wherever it is cloned, including a
# compose deployment (Dockhand, Portainer) whose builder may be the classic one. The publish is
# framework-dependent and RID-less, so the same build serves amd64 and arm64.

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG VERSION=1.0.0
WORKDIR /src

# Restore first, from the project files alone, so a code change does not throw the package layer away.
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/ShinyFileServe.Shared/ShinyFileServe.Shared.csproj src/ShinyFileServe.Shared/
COPY src/ShinyFileServe.Core/ShinyFileServe.Core.csproj src/ShinyFileServe.Core/
COPY src/ShinyFileServe.Server/ShinyFileServe.Server.csproj src/ShinyFileServe.Server/
COPY src/ShinyFileServe.Web/ShinyFileServe.Web.csproj src/ShinyFileServe.Web/
COPY heads/ShinyFileServe.Headless/ShinyFileServe.Headless.csproj heads/ShinyFileServe.Headless/
RUN dotnet restore heads/ShinyFileServe.Headless/ShinyFileServe.Headless.csproj \
 && dotnet restore src/ShinyFileServe.Web/ShinyFileServe.Web.csproj

COPY src/ src/
COPY heads/ShinyFileServe.Headless/ heads/ShinyFileServe.Headless/

# The server project publishes the portal and embeds it as it builds (see ShinyFileServe.Server.csproj).
RUN dotnet publish heads/ShinyFileServe.Headless/ShinyFileServe.Headless.csproj \
      -c Release -o /app --no-restore --nologo \
      -p:Version=$VERSION -p:UseAppHost=false \
 && find /app/runtimes -mindepth 1 -maxdepth 1 \! -name 'linux-x64' \! -name 'linux-arm64' -exec rm -rf {} +

FROM mcr.microsoft.com/dotnet/runtime:11.0
ARG VERSION=1.0.0
LABEL org.opencontainers.image.title="FileServe" \
      org.opencontainers.image.description="Self-hosted file server for a team, with a browser portal" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.source="https://github.com/ShinySoftware/ShinyFileServe"

# PUID/PGID/UMASK follow the convention Unraid and most NAS images use: the server drops to that user
# so files it writes into /shares belong to someone the host's own SMB/NFS users can work with.
# 99:100 is Unraid's nobody:users.
ENV FILESERVE_DATA=/data \
    FILESERVE_PORT=8080 \
    FILESERVE_PLACES=/shares \
    HOME=/data \
    PUID=99 \
    PGID=100 \
    UMASK=002 \
    DOTNET_gcServer=0

WORKDIR /app
COPY --from=build /app ./
COPY docker/entrypoint.sh /entrypoint.sh

RUN chmod 0755 /entrypoint.sh && mkdir -p /data /shares

VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
