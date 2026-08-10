# Builds both SPAs and serves them from Caddy. Build context is the repository root.
# syntax=docker/dockerfile:1

FROM node:24-alpine AS build
WORKDIR /src

ENV PNPM_HOME=/pnpm
ENV PATH=$PNPM_HOME:$PATH
RUN corepack enable

# Manifests first so the install layer caches independently of source changes.
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
COPY frontend/apps/console/package.json   apps/console/
COPY frontend/apps/handheld/package.json  apps/handheld/
COPY frontend/packages/api-client/package.json packages/api-client/
COPY frontend/packages/ui/package.json         packages/ui/

RUN --mount=type=cache,id=pnpm,target=/pnpm/store \
    pnpm install --frozen-lockfile

COPY frontend/ ./
RUN pnpm --filter @countflow/console build && pnpm --filter @countflow/handheld build

# No Node at runtime — the SPAs are static bundles. This is what the Vite
# decision bought us (see docs/adr/0003).
FROM caddy:2-alpine AS runtime

COPY --from=build /src/apps/console/dist   /srv/console
COPY --from=build /src/apps/handheld/dist  /srv/handheld
COPY docker/Caddyfile /etc/caddy/Caddyfile

EXPOSE 80 443
