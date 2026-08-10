# Countflow API. Build context is the repository root.
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Project files first, so the restore layer survives source changes and only
# invalidates when a dependency actually moves.
#
# The NuGet cache mount uses sharing=locked. BuildKit defaults to
# sharing=shared, and compose builds this image and the migrator concurrently —
# two `dotnet restore` processes writing one global-packages folder corrupt each
# other's temp extractions. Locking serialises them while still sharing the cache.
COPY backend/Directory.Build.props backend/Directory.Packages.props ./
COPY backend/src/Countflow.Api/Countflow.Api.csproj                                   src/Countflow.Api/
COPY backend/src/Countflow.Bootstrap/Countflow.Bootstrap.csproj                       src/Countflow.Bootstrap/
COPY backend/src/Countflow.SharedKernel/Countflow.SharedKernel.csproj                 src/Countflow.SharedKernel/
COPY backend/src/Modules/Countflow.Modules.Identity/Countflow.Modules.Identity.csproj     src/Modules/Countflow.Modules.Identity/
COPY backend/src/Modules/Countflow.Modules.MasterData/Countflow.Modules.MasterData.csproj src/Modules/Countflow.Modules.MasterData/

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Countflow.Api/Countflow.Api.csproj

COPY backend/src/ src/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Countflow.Api/Countflow.Api.csproj \
        -c "$BUILD_CONFIGURATION" \
        -o /app \
        --no-restore

# Chiseled: no shell, no package manager, and runs as a non-root user (UID 1654)
# without any extra configuration.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1

COPY --from=build /app .

EXPOSE 8080

# Re-invokes this same binary in probe mode; see --healthcheck in Program.cs.
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
    CMD ["/app/Countflow.Api", "--healthcheck"]

ENTRYPOINT ["/app/Countflow.Api"]
