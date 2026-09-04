# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# A separate `dotnet restore` (cached via just the .csproj files) followed by
# `dotnet publish --no-restore` looked like the standard layer-caching optimization, but for this
# project it silently produced a broken image: the published static web assets manifest
# (LidarrCompanion.Web.staticwebassets.endpoints.json) came out missing blazor.web.js entirely -
# no build error, no warning, just a 404 on that file at runtime with zero client-side Blazor
# interactivity as a result (every button/click on every page silently did nothing - discovered
# via a live CDP-driven browser session, not a hunch). A single `dotnet publish` with its own
# implicit restore, run only after the full source is present, does not have this problem -
# confirmed directly against `dotnet publish`'s own output. Costs a bit of rebuild time since
# NuGet restore can't be cached independently of source changes any more; that's the right trade
# for a build that's actually correct. The old WPF app and its test project are intentionally left
# out of the build context entirely (see .dockerignore) - only Core and Web are ever needed here.
COPY LidarrCompanion.Core/ LidarrCompanion.Core/
COPY LidarrCompanion.Web/ LidarrCompanion.Web/
RUN dotnet publish LidarrCompanion.Web/LidarrCompanion.Web.csproj -c Release -o /app

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
