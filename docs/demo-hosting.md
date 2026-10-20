# Public static demo hosting

This is the deployment guide for the lightweight Countflow demo. The existing
console and handheld run with private browser-local warehouse data. The server
serves files; it does not run the API, PostgreSQL, Seq, or the simulator.

| Host | Deployment method |
|---|---|
| [Dokploy](#dokploy) | Existing `compose.demo-static.yaml`, domain → `web:80` |
| [Coolify](#coolify) | Same Compose file and internal port |
| [Docker on a VPS](#docker-on-a-vps) | Existing `static-demo` image target, TLS reverse proxy |
| [Nginx](#nginx) | Combined static directory with two SPA fallbacks |
| [Caddy](#caddy) | Combined static directory, automatic HTTPS |
| [Netlify](#netlify) | Static build plus redirects and cache headers |
| [Vercel](#vercel) | Static build plus ordered filesystem/SPA routes |

## Requirements shared by every host

- Use a dedicated HTTPS hostname, such as `demo.example.com`, separate from the
  real application. DNS and certificate setup belong to the chosen host.
- Keep the console at `/` and handheld at `/handheld/` on **the same origin**.
  Hosting under `/countflow/` or on two separate domains requires code changes.
- Compile both apps in demo mode. Setting a runtime environment variable after
  building does not change an already built bundle.
- Route console deep links to `/index.html` and handheld deep links to
  `/handheld/index.html`. One catch-all pointing to the console breaks handheld.
- Reject `/api` and `/api/*`. Never attach a real backend or proxy those paths.
- Revalidate HTML, the handheld service worker, Workbox scripts and manifest.
  Only fingerprinted asset directories should receive immutable caching.

Visitors choose admin, supervisor, or operator with the existing sign-in buttons;
there is no real demo password or server credential to configure. Their changes
remain in their own browser profile. Console and handheld tabs share that state.
Reset demo clears demo state and pending commands, preserving language and theme.
After 60 minutes without interaction, opening or returning to the demo resets it.

## Build the static directory

For file servers and static hosting providers, use Node.js 24 and the pnpm version
pinned in `frontend/package.json`. Run from the repository root:

```sh
corepack enable
cd frontend
pnpm install --frozen-lockfile
pnpm build:demo
```

The cross-platform build script type-checks and builds both apps with
`VITE_DEMO_MODE=true`, then replaces only the generated `frontend/dist/` directory:

```text
frontend/dist/
  index.html                 console
  assets/                    console fingerprinted assets
  handheld/
    index.html               handheld
    assets/                  handheld fingerprinted assets
    sw.js                    service worker
    manifest.webmanifest
    workbox-*.js
  health/ready.json           static health response
  404.html                   static error response
```

Upload **the contents** of `frontend/dist/` to the document root, including all
handheld files. A deployment needs no runtime Node process, secrets, API URL,
database, persistent volume, or scheduled reset job. Normal `pnpm build` remains
the backend-connected build; use `pnpm build:demo` for this public demo.

## Dokploy

1. Create a **Docker Compose** application from this repository.
2. Set Compose Path to `compose.demo-static.yaml`, with the repository root as
   build context. The file already selects `static-demo` and enables demo mode.
3. Add the demo domain, choose service `web`, container port `80`, path `/`,
   and enable HTTPS. Point DNS at the server.
4. Deploy. Check `/health/ready` and `/handheld/` on the public hostname.

Leave Isolated Deployments off (Dokploy now marks it deprecated) and leave the
Networks section at its default. `web` is the only service, so it needs no
internal network; the domain attaches it to `dokploy-network` for Traefik.

No host port or environment settings are needed. The included Caddy listener uses
HTTP internally because Dokploy terminates TLS. Do not choose
`compose.dokploy.yaml`: that is the separate full backend demo.

Reference: [Dokploy Compose domain routing](https://docs.dokploy.com/docs/core/docker-compose/domains).

## Coolify

Create a Git-backed Docker Compose resource with repository root `/` and Compose
file `compose.demo-static.yaml`. Assign `https://demo.example.com` to service
`web`, targeting its internal port `80`; use the platform's port selection or
its documented domain port suffix when needed. Enable the HTTPS redirect and
deploy. Do not publish database services or add a backend resource.

Keep the file's build target and argument unchanged. The container listens on
HTTP port 80 behind Coolify's proxy. Set any optional HTTP health check to
`/health/ready` on port 80. No persistent storage is required.

Reference: [Coolify Docker Compose](https://coolify.io/docs/applications/builds/docker-compose).

## Docker on a VPS

Build from the repository root. Both the target and build argument are required:

```sh
docker build -f docker/frontend.Dockerfile --target static-demo \
  --build-arg VITE_DEMO_MODE=true -t countflow-demo:local .
docker run -d --name countflow-demo --restart unless-stopped \
  -p 127.0.0.1:8080:80 --memory 128m --cpus 0.5 --pids-limit 64 \
  --read-only --tmpfs /config:rw,size=4m --tmpfs /data:rw,size=4m \
  --log-driver json-file --log-opt max-size=5m --log-opt max-file=2 \
  countflow-demo:local
curl --fail http://127.0.0.1:8080/health/ready
```

Terminate public HTTPS with your existing reverse proxy and forward the entire
hostname, preserving paths, to `http://127.0.0.1:8080`. For example, a **host-side**
Caddy installation can use:

```caddyfile
demo.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

If the proxy itself runs in Docker, use a shared Docker network and the service's
internal port instead of container-local `127.0.0.1`. The Compose file intentionally
has `expose`, without a host `ports` binding: it expects a platform proxy.

The 128 MiB setting is a configurable runtime ceiling, not measured consumption.
Build stages need more memory. To measure serving usage after real requests:

```sh
docker stats --no-stream countflow-demo
```

Use versioned image tags for releases. Replace the container with the new image;
rollback uses the previous tag. There is no server-side visitor database to migrate.

## Nginx

Build once, then copy `frontend/dist/` contents into `/var/www/countflow-demo/`.
Use a user-readable directory outside a private home folder. Install a certificate
for your hostname using your server's certificate tooling before enabling the TLS
block below. Replace the hostname and certificate paths with your actual values.

Place this configuration in a file included by Nginx's `http` block, such as
`/etc/nginx/conf.d/countflow-demo.conf`. The installation must load its standard
`mime.types` file in `http`, so JavaScript and CSS receive the right MIME types.

```nginx
server {
    listen 80;
    server_name demo.example.com;
    return 308 https://demo.example.com$request_uri;
}

server {
    listen 443 ssl;
    server_name demo.example.com;
    ssl_certificate /etc/letsencrypt/live/demo.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/demo.example.com/privkey.pem;

    root /var/www/countflow-demo;
    index index.html;
    server_tokens off;
    # Includes HTML, sw.js, Workbox and the manifest unless overridden below.
    add_header Cache-Control "no-cache" always;
    # Visitor data is local. Avoid accumulating access logs on this demo host.
    access_log off;

    location = /api { return 404; }
    location ^~ /api/ { return 404; }
    location = /health/ready {
        default_type application/json;
        return 200 '{"status":"Healthy"}';
    }
    location = /handheld { return 308 /handheld/; }

    location ~ ^/(handheld/)?assets/ {
        try_files $uri =404;
        add_header Cache-Control "public, max-age=31536000, immutable";
    }
    # Missing scripts/images must not receive the SPA's HTML response.
    location ~* \.(js|css|map|json|webmanifest|svg|png|ico|woff2?)$ {
        try_files $uri =404;
    }
    location /handheld/ {
        try_files $uri $uri/ /handheld/index.html;
    }
    location / {
        try_files $uri $uri/ /index.html;
    }
}
```

Validate and reload with your server's service manager, for example on systemd:

```sh
sudo nginx -t
sudo systemctl reload nginx
```

If an existing load balancer terminates HTTPS, use an internal HTTP listener
instead of the TLS/redirect blocks, and restrict direct access to that listener.
Rotate Nginx error logs through the OS logging policy. Upload files as a complete
release, then switch the document root or release symlink after the upload ends.

Reference: [Nginx `root`, `location`, and `try_files`](https://nginx.org/en/docs/http/ngx_http_core_module.html#try_files).

## Caddy

To serve files directly without Docker, copy the combined build into
`/var/www/countflow-demo/` and use this Caddyfile. Point DNS to the host and allow
ports 80 and 443. Caddy manages the public certificate.

```caddyfile
demo.example.com {
    root * /var/www/countflow-demo
    encode zstd gzip
    header Cache-Control "no-cache"

    # Preserve this order: static files before the two SPA fallbacks.
    route {
    @api path /api /api/*
    handle @api {
        respond "Not found" 404
    }
    handle /health/ready {
        header Content-Type application/json
        respond `{"status":"Healthy"}` 200
    }
    redir /handheld /handheld/ 308

    @assets path /assets/* /handheld/assets/*
    handle @assets {
        header Cache-Control "public, max-age=31536000, immutable"
        file_server
    }
    @files path_regexp files \.(js|css|map|json|webmanifest|svg|png|ico|woff2?)$
    handle @files {
        file_server
    }
    handle /handheld/* {
        try_files {path} /handheld/index.html
        file_server
    }
    handle {
        try_files {path} /index.html
        file_server
    }
    }
}
```

Validate with `caddy validate --config /etc/caddy/Caddyfile`, then reload using
your installation's service manager. Access logging is not enabled in this
example; keep runtime logs bounded through the service manager.

Reference: [Caddy SPA patterns](https://caddyserver.com/docs/caddyfile/patterns#single-page-apps-spas).

## Netlify

Connect the repository or upload the combined directory after building locally.
For Git builds, use Node.js 24 and add this **repository-root** `netlify.toml`:

```toml
[build]
  base = "frontend"
  command = "pnpm build:demo"
  publish = "dist"

[build.environment]
  NODE_VERSION = "24"

[[redirects]]
  from = "/api"
  to = "/404.html"
  status = 404
  force = true

[[redirects]]
  from = "/api/*"
  to = "/404.html"
  status = 404
  force = true

[[redirects]]
  from = "/health/ready"
  to = "/health/ready.json"
  status = 200

[[redirects]]
  from = "/handheld/assets/*"
  to = "/404.html"
  status = 404

[[redirects]]
  from = "/assets/*"
  to = "/404.html"
  status = 404

[[redirects]]
  from = "/handheld/*"
  to = "/handheld/index.html"
  status = 200

[[redirects]]
  from = "/*"
  to = "/index.html"
  status = 200

[[headers]]
  for = "/*"
  [headers.values]
    Cache-Control = "no-cache"
```

Non-forced rules preserve existing files, including handheld's worker and assets.
Keep Netlify's Pretty URLs enabled for directory URL normalization; an explicit
forced `/handheld` → `/handheld/` redirect can loop because Netlify normalizes
those paths before matching rules.
This configuration conservatively revalidates all files; keep that policy until
any CDN-specific asset caching changes are verified. Add your custom domain and
enable HTTPS in Netlify. No functions or backend are needed.

For a manual drag-and-drop deployment, TOML is not part of the uploaded output:
translate the same rules into `dist/_redirects` and `dist/_headers` before upload,
or use the Git deployment above so Netlify reads the configuration automatically.

References: [rewrite rules](https://docs.netlify.com/manage/routing/redirects/rewrites-proxies/),
[rule options](https://docs.netlify.com/manage/routing/redirects/redirect-options/),
[headers](https://docs.netlify.com/manage/routing/headers/).

## Vercel

Import the repository, select **Other** as the framework preset, set Root Directory
to `frontend`, and choose Node.js 24.x. Use install command
`pnpm install --frozen-lockfile`. Add **`frontend/vercel.json`**:

```json
{
  "$schema": "https://openapi.vercel.sh/vercel.json",
  "buildCommand": "pnpm build:demo",
  "outputDirectory": "dist",
  "routes": [
    {
      "src": "/.*",
      "headers": { "Cache-Control": "no-cache" },
      "continue": true
    },
    { "src": "/api(?:/.*)?", "status": 404, "dest": "/404.html" },
    { "src": "/health/ready", "dest": "/health/ready.json" },
    { "src": "/handheld", "status": 308, "headers": { "Location": "/handheld/" } },
    { "handle": "filesystem" },
    { "src": "/(?:handheld/)?assets/.*", "status": 404, "dest": "/404.html" },
    { "src": "/handheld/.*", "dest": "/handheld/index.html" },
    { "src": "/.*", "dest": "/index.html" }
  ]
}
```

Ordered routes reject API paths before filesystem checks, preserve actual files,
then apply the two SPA fallbacks. Do not combine this `routes` configuration with
top-level `rewrites`, `redirects`, `headers`, `cleanUrls`, or `trailingSlash`.
Add the custom domain in Vercel and verify HTTPS. No serverless functions are used.

Reference: [Vercel configuration and routing](https://vercel.com/docs/project-configuration/vercel-json#routes).

## Other hosts

An Apache, object-storage/CDN, or other static host can serve the same directory
if it implements the routing and cache requirements above. Do not enable just a
single global SPA fallback. GitHub project Pages requires base-path changes and
does not provide these rewrite controls, so it is not a drop-in target here.

Cloudflare Pages supports SPA rewrites, but its `_redirects` file cannot issue
the custom 404 rewrites used to exclude API paths from these catch-alls. A
Cloudflare-specific routing/Worker configuration is therefore needed before
claiming equivalent support; the Netlify rules must not be copied there unchanged.
See [Cloudflare's supported redirect codes](https://developers.cloudflare.com/pages/configuration/redirects/).

## Verify a deployment

Use the deployed hostname, not just a local preview:

```sh
curl -I https://demo.example.com/
curl -I https://demo.example.com/stock
curl -I https://demo.example.com/handheld/
curl -I https://demo.example.com/handheld/tasks/example
curl -I https://demo.example.com/handheld/sw.js
curl -i https://demo.example.com/api/v1/unknown
curl --fail https://demo.example.com/health/ready
```

The first four return HTML successfully; handheld routes must contain the
handheld shell, not console HTML. The worker must return JavaScript, with
revalidation rather than immutable caching. Unknown API paths return 404 and
never reach a real backend. The health response is static JSON, not a database
health check. Also open a real generated asset URL and confirm its MIME type.

In the browser, refresh a console deep link and `/handheld/`, try each role,
and complete a receipt/putaway. Confirm the changed stock appears in both apps
and survives refresh. A separate browser profile must start with fresh data.
Reset from either app should invalidate the old demo session and pending work,
while preserving language and theme. Check the Network tab for unexpected API or
external service requests.

## Updates, storage, and troubleshooting

- Publish both apps together. Keep the preceding release's fingerprinted assets
  during rollout where possible so open tabs can still load their old chunks.
  Use the provider's atomic deployments or a complete directory/symlink switch.
- Never cache HTML or `/handheld/sw.js` immutably. If an existing CDN rule caches
  everything, remove it and purge those entries. The handheld service worker
  also has its own update lifecycle; a browser reset is not a server deployment.
- A deploy does not necessarily reset existing browser data; reset explicitly
  through the app when inspecting fresh fixtures. There is no server directory
  of visitor edits to clean up.
- Blank page or JavaScript MIME errors usually mean missing files were rewritten
  to HTML. Check both asset directories and the publish root.
- Handheld displaying the console means the root fallback ran first. Restore
  the dedicated handheld fallback before the console fallback.
- Real API requests or missing role buttons indicate the wrong build. Rebuild
  with `pnpm build:demo`, or select Docker target `static-demo` with the demo flag.
- For self-hosted servers, bound container/system logs. Static CDN access and
  analytics retention are managed separately by the provider.

The full backend demo has separate deployment instructions in
[API hosting](api/hosting.md#full-demo-stack-on-dokploy). Its credentials,
database reset process, and Compose files do not apply to this static demo.
