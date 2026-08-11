# Countflow API. Build context is the repository root.
# syntax=docker/dockerfile:1

# Every project file, and nothing else.
#
# Collected generically rather than listed one COPY per project. Listing them was
# the earlier shape, and it quietly made "adding a module is one line in
# ModuleCatalog" untrue: Phase 3 added three modules and the container build
# broke on all three, in a file nobody thinks to look in. This stage is cheap and
# rebuilds every time — what matters is that the archive it produces only
# *changes* when a project file does, and that is what the restore layer is keyed
# on. migrator.Dockerfile does the same thing for the same reason.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS projects
WORKDIR /ctx
COPY backend/ ./
RUN find . -path ./tests -prune -o \
        \( -name '*.csproj' -o -name 'Directory.*.props' \) -print \
    | tar -cf /projects.tar --files-from -

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
COPY --from=projects /projects.tar /tmp/projects.tar
RUN tar -xf /tmp/projects.tar -C /src && rm /tmp/projects.tar

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
