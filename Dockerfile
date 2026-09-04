# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the two project files this app actually needs first, so `dotnet restore` is cached
# across rebuilds that only touch source (not csproj/package references). The old WPF app and its
# test project are intentionally left out of the build context entirely (see .dockerignore) -
# only Core and Web ever needed for the container.
COPY LidarrCompanion.Core/LidarrCompanion.Core.csproj LidarrCompanion.Core/
COPY LidarrCompanion.Web/LidarrCompanion.Web.csproj LidarrCompanion.Web/
RUN dotnet restore LidarrCompanion.Web/LidarrCompanion.Web.csproj

COPY LidarrCompanion.Core/ LidarrCompanion.Core/
COPY LidarrCompanion.Web/ LidarrCompanion.Web/
RUN dotnet publish LidarrCompanion.Web/LidarrCompanion.Web.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Runs as root, not the base image's default non-root "app" user: this container needs read/write
# access to whatever host paths get bind-mounted for the music library/import/backup folders
# (see docker-compose.yml), and those are typically owned by whatever user/UID already manages
# them on the NAS host - not a UID chosen by this image. This is a single-user, LAN-only,
# password-gated tool (see AdminPasswordHash in settings), not a multi-tenant service, so the
# usual "don't run containers as root" tradeoff leans the other way here. If you'd rather map a
# specific host UID/GID in, add that mapping yourself (docker-compose `user:` or an entrypoint
# script) instead of relying on this default.
USER root

ENV ASPNETCORE_URLS=http://+:8080
ENV DataDirectory=/data
EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "LidarrCompanion.Web.dll"]
