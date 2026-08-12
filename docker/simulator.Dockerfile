# Countflow warehouse simulator — a *client* of the API, not part of it.
# Build context is the repository root.
# syntax=docker/dockerfile:1
#
# This image is what makes ADR 0006 literal. The demo warehouse is not seeded: it
# is built by a container that signs in with an API key and drives the published
# endpoints, exactly as an outside integrator would. Everything a visitor sees on
# the demo is therefore a state a real operator could have reached — which is a
# much stronger claim than a fixture can make, and it costs one Dockerfile.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS projects
WORKDIR /ctx
COPY backend/ ./
RUN find . -path ./tests -prune -o \
        \( -name '*.csproj' -o -name 'Directory.*.props' \) -print \
    | tar -cf /projects.tar --files-from -

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# sharing=locked on the NuGet cache: compose may build this alongside the api and
# migrator images, and three concurrent restores into one global-packages folder
# corrupt each other's temp extractions. See api.Dockerfile.
COPY --from=projects /projects.tar /tmp/projects.tar
RUN tar -xf /tmp/projects.tar -C /src && rm /tmp/projects.tar

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Countflow.Simulator/Countflow.Simulator.csproj

COPY backend/src/ src/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Countflow.Simulator/Countflow.Simulator.csproj \
        -c "$BUILD_CONFIGURATION" \
        -o /app \
        --no-restore

# runtime, not aspnet: the simulator references nothing at all (ADR 0006), so
# unlike the migrator it carries no FrameworkReference on ASP.NET Core and gets
# the smaller base for free. That the boundary shows up in the image size is a
# pleasant accident, but it is not nothing.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build /app .

ENTRYPOINT ["/app/Countflow.Simulator"]
