# Countflow migrator — runs every module's migrations and seeders, then exits.
# Build context is the repository root.
# syntax=docker/dockerfile:1

# Every project file, and nothing else. Collected generically rather than listed
# one COPY per project, so that adding a module really is the single line in
# ModuleCatalog it is advertised to be — see the longer note in api.Dockerfile.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS projects
WORKDIR /ctx
COPY backend/ ./
RUN find . -path ./tests -prune -o \
        \( -name '*.csproj' -o -name 'Directory.*.props' \) -print \
    | tar -cf /projects.tar --files-from -

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# sharing=locked on the NuGet cache: this image builds concurrently with the API
# image and they share one global-packages folder. See api.Dockerfile.
COPY --from=projects /projects.tar /tmp/projects.tar
RUN tar -xf /tmp/projects.tar -C /src && rm /tmp/projects.tar

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Countflow.Migrator/Countflow.Migrator.csproj

COPY backend/src/ src/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Countflow.Migrator/Countflow.Migrator.csproj \
        -c "$BUILD_CONFIGURATION" \
        -o /app \
        --no-restore

# The runtime image carries no EF design-time tooling: migrations are compiled
# into the module assemblies and applied through MigrateAsync, so `dotnet ef`
# (and the ~1.3 GB SDK it needs) never has to ship.
#
# aspnet rather than the smaller runtime image: the migrator references the
# modules, and ICountflowModule.MapEndpoints takes an IEndpointRouteBuilder, so
# SharedKernel carries a FrameworkReference on Microsoft.AspNetCore.App. Loading
# those assemblies needs the ASP.NET Core shared framework even though the
# migrator never serves a request. (Splitting SharedKernel into core and web
# halves would avoid this — worth doing if image size ever matters.)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build /app .

ENTRYPOINT ["/app/Countflow.Migrator"]
