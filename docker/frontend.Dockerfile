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
COPY frontend/packages/i18n/package.json       packages/i18n/
COPY frontend/packages/demo/package.json       packages/demo/

RUN --mount=type=cache,id=pnpm,target=/pnpm/store \
    pnpm install --frozen-lockfile

COPY frontend/ ./

# Empty on every build but the demo's. Vite inlines VITE_-prefixed values at
# build time, so the demo image carries its sign-in hints and a production image
# has no way to grow them — which is the point: the login screen of a real
# deployment is a door, and a door does not list its own keys.
ARG VITE_DEMO_ACCOUNTS=""
ENV VITE_DEMO_ACCOUNTS=$VITE_DEMO_ACCOUNTS
ARG VITE_DEMO_MODE=false
ENV VITE_DEMO_MODE=$VITE_DEMO_MODE

RUN pnpm --filter @countflow/console build && pnpm --filter @countflow/handheld build

# Explicit target for the lightweight public demo. The default final target
# remains the real application gateway below.
FROM caddy:2-alpine AS static-demo
COPY --from=build /src/apps/console/dist /srv/console
COPY --from=build /src/apps/handheld/dist /srv/handheld
COPY docker/Caddyfile.demo /etc/caddy/Caddyfile
EXPOSE 80

# No Node at runtime — the SPAs are static bundles. This is what the Vite
# decision bought us (see docs/adr/0003).
FROM caddy:2-alpine AS runtime

COPY --from=build /src/apps/console/dist   /srv/console
COPY --from=build /src/apps/handheld/dist  /srv/handheld
COPY docker/Caddyfile /etc/caddy/Caddyfile

EXPOSE 80 443
