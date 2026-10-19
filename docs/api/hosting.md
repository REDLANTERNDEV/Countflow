# Hosting: the demo stack, and pointing it at a domain

## Lightweight interactive demo on Dokploy

Use **`compose.demo-static.yaml`** and route the demo domain to service **web**,
container port **80**, with HTTPS enabled. This serves the console and handheld
with private browser-local data and no backend services.

The consolidated [static demo hosting guide](../demo-hosting.md) covers Dokploy,
Coolify, standalone Docker, Nginx, Caddy, Netlify, and Vercel, including the shared
build command, routing, caching, deployment checks, and rollback.

## Full demo stack on Dokploy

Use **Docker Compose**, with the repository root as build context and
`compose.dokploy.yaml` as the Compose Path. This file imports the existing demo
services through Compose `extends`; it is independently deployable and does not
load the development overlay. Docker Stack is unsuitable because this deployment
builds images and uses `extends`.

In Dokploy's Environment tab, supply these settings before deployment:

| Setting | Value |
|---|---|
| `POSTGRES_PASSWORD`, `JWT_SIGNING_KEY`, `SEQ_ADMIN_PASSWORD` | Separate random secrets, at least 32 bytes each. |
| `WEBHOOKS_ENCRYPTION_KEY` | A separate base64-encoded 32-byte key (`openssl rand -base64 32`). Keep it stable across deployments. |
| `DEMO_ADMIN_API_KEY`, `DEMO_SUPERVISOR_API_KEY`, `DEMO_OPERATOR_API_KEY` | Three separate random machine keys, each prefixed `cfk_`. Keep these private. |
| `DEMO_PASSWORD` | Public sign-in secret for `admin`, `supervisor` and `operator`, for example `Countflow#2026`. It appears in the demo bundles. |
| `CADDY_SITE_ADDRESS` | `:80`. Dokploy terminates HTTPS; Caddy serves HTTP internally. |
| `DEMO_WAREHOUSE`, `DEMO_DAYS`, `DEMO_SEED` | Optional; defaults are `WH1`, `4`, `20260812`. |

For the other random secrets, use a fresh invocation of
`openssl rand -base64 32 | tr '+/' '-_' | tr -d '=\n'` for each value. Prefix
each machine key with `cfk_`. Never reuse credentials from another deployment.

Enable **Isolated Deployments**. Add the desired hostname in the Domains tab,
select service **web**, container port **80**, path `/`, and enable HTTPS. Point
DNS at the Dokploy server. Preview Compose before deploying: all six services
must remain connected, only `web` should receive domain routing, and no service
should bind a host port. Caddy must remain on `:80` to avoid a second TLS redirect
behind Dokploy's proxy.

Deploy the committed revision containing this setup. The migrator must exit
successfully before the API becomes healthy; the simulator then builds the
warehouse through its public APIs. Wait for its successful exit before sharing
the demo. A later deployment can rerun it and add more activity.

Verify the public hostname after deployment: HTTPS and its HTTP redirect,
`/health/ready`, console sign-in for each demo role, handheld sign-in at
`/handheld/`, visible stock and completed work, and a normal receipt/putaway.
Confirm the database, API and Seq have no published ports. Local Compose tests
do not prove public DNS, certificates or Dokploy's injected routing.

This follows Dokploy's [Compose configuration](https://docs.dokploy.com/docs/core/docker-compose)
and [domain routing](https://docs.dokploy.com/docs/core/docker-compose/domains)
guidance. Domain labels and network wiring remain owned by Dokploy.

### Demo reset policy

The demo is shared, disposable warehouse data. Initially, resets are explicit
operator actions, with no automatic destructive schedule. Visitors can change
what everyone sees; no real business data belongs here. Keep the public sign-in
secret separate from the private infrastructure and machine credentials.

`scripts/demo-reset.sh` requires `--confirm-demo-reset` and identifies the database
using both its Compose project labels and the `countflow.demo=true` volume marker.
It refuses an unmarked or ambiguous database. It stops the demo, removes only that
database volume, and preserves Caddy's TLS state and Seq logs.

For a standalone demo, pass the same configuration options used at startup:
`scripts/demo-reset.sh --confirm-demo-reset --env-file YOUR_DEMO_SETTINGS`.
The script explicitly selects the base and demo files, then rebuilds the stack.

For Dokploy, set `DEMO_PROJECT_NAME` to the exact project name shown in Preview
Compose and run the reset from the deployment checkout with
`scripts/demo-reset.sh --confirm-demo-reset --dokploy` and the same configuration
settings. This mode deliberately leaves the stack stopped. **Redeploy through
Dokploy** to rebuild the warehouse with its domain labels and network wiring;
starting the raw file directly would lose those generated settings. Expect an
outage during reset and discard old demo sessions and queued handheld commands.

Existing volumes created before the demo marker was added are refused; inspect
and handle those manually rather than treating an unmarked database as disposable.

## One command

```bash
./scripts/demo.sh
```

On first run it generates `.env.demo` — a Postgres password, a JWT signing key, a
Seq password, a webhook encryption key and three API keys, all from `openssl rand` — and then brings the
stack up. On every run after that it reuses them, so the database, the sessions
and the credentials survive a restart.

`.env.demo` is gitignored and mode `600`. It holds three machine credentials; do
not commit it, and do not reuse any value in it anywhere else.

By default `CADDY_SITE_ADDRESS` is `:80`, which serves plain HTTP on every
hostname. That is right for a laptop and wrong for the internet — see below.

**The demo runs as its own compose project**, `countflow-demo`, with its own
containers and its own volumes. So it sits happily beside a development stack on
the same machine — the dev stack is on `:8000`, the demo on `:80` — and neither
can reach the other's database. That separation is deliberate: without it the
demo would adopt whatever database was already there and seed three admin-role
API keys into it.

## What comes up

| Service | Does |
|---|---|
| `postgres` | One database, one schema per module. Not published. |
| `migrator` | Runs every module's migrations and seeders, then exits. |
| `api` | The published surface. Not published — Caddy is the only way in. |
| `simulator` | **Builds the warehouse, through the API.** Runs once and exits. |
| `web` | Caddy, serving both SPAs and reverse-proxying `/api`. Ports 80 and 443. |
| `seq` | Structured logs. Not published; password in `.env.demo`. |

## The simulator is the point

The demo warehouse is **not seeded**. What the migrator seeds is the topology and
the catalogue — 2,245 locations and 500 SKUs, which are what a warehouse *is*
rather than what happened in it. Everything that happened is the `simulator`
service's work: it signs in with an API key and books deliveries, does the jobs on
the queue, ships the orders and counts the racking through the endpoints a
handheld and a console use.

`Countflow.Simulator` has no project references at all ([decision 6](../decisions.md#6-the-simulator-is-a-client-not-a-module)),
so it cannot reach past a module boundary even by accident. Every state a visitor
sees is therefore one a real operator could have reached — which is a much
stronger claim than a fixture can make, and is why the demo is built this way
rather than by a bigger seeder.

It holds **three** credentials, one per role, because promoting a rule set is
admin-only, releasing a wave is supervisor-and-above and confirming a task is an
operator's job. A single all-powerful integration would sail through all three and
notice nothing if one of those checks were wrong.

Re-running it adds more days to the same warehouse rather than starting over:

```bash
docker compose --env-file .env.demo -f compose.yaml -f compose.demo.yaml \
  run --rm simulator --url http://api:8080 --warehouse WH1 --days 3 --seed 20260901
```

The same seed builds the same warehouse, so a demo somebody was shown last week
and the one they come back to are the same building.

### The counting policy is different on a demo

`count.settled_stock` will not count a bay within four hours of anything moving in
it — correct, because counting a bay mid-putaway measures the truck — and it means
a warehouse built ninety seconds ago has nothing countable at all. The simulator
solves that the way the product intends: it clones the cycle-count rule set, sets
the quiet period to nothing, scopes the copy to its own warehouse and promotes it.

**So a simulated warehouse runs a different counting policy from the shipped
default.** That is visible on the Rules screen, and `--keep-quiet-period` turns it
off.

## Signing in

The login screen of a demo build lists its accounts and fills them in on a click.
That list comes from `VITE_DEMO_ACCOUNTS`, a **build argument** inlined by Vite at
build time — an endpoint answering "which credentials work here" would exist in
every deployment and be one misconfiguration away from answering it in production.
A variable absent from the production build cannot.

`admin`, `supervisor` and `operator`, all on `DEMO_PASSWORD`. The demo is public
and shared: anything a visitor changes, everybody sees.

## Pointing it at a domain

1. Point an `A` record at the host.
2. Set `CADDY_SITE_ADDRESS` in `.env.demo` to that hostname — bare, no scheme.
3. Open inbound 80 and 443. Port 80 is not optional: the ACME HTTP challenge
   needs it, and it is also what redirects visitors to HTTPS.
4. `./scripts/demo.sh` again.

Caddy provisions and renews the certificate on its own; there is nothing else to
configure and no certificate to rotate by hand. The `caddy-data` volume holds the
account key and the certificates, so do not delete it casually — you will re-issue
on every restart and meet Let's Encrypt's rate limits.

**Provisioning the host and the DNS is outside what this repository does.** What is
delivered here is a stack that comes up with one command and the four steps above.

## Rate limits on a public host

The demo overlay turns the limiter on and down: 300 requests a minute per
credential, 30 for an anonymous caller. Generous enough that a person clicking
around never meets it, far short of what a script would want. `DEMO_RATE_LIMIT`
changes the first.

Requests are counted per *credential*, so one noisy visitor cannot exhaust
anybody else's budget.

## A real deployment

Use `compose.prod.yaml` instead of `compose.demo.yaml`. It is the same stack
without the simulator, without demo users, and without the sign-in hints baked
into the front end. Both overlays name `compose.yaml` explicitly, which is what
skips `compose.override.yaml` — the development conveniences (a published database
port, a known signing key, demo seeding) cannot reach a deployed environment by
accident.

```bash
export POSTGRES_PASSWORD=… JWT_SIGNING_KEY=… SEQ_ADMIN_PASSWORD=… CADDY_SITE_ADDRESS=…
docker compose -f compose.yaml -f compose.prod.yaml up -d
```

Every one of those is `:?` in the overlay, so a missing value stops the stack
rather than silently defaulting.

## Operating notes

**Logs.** Seq is on the compose network only. To read it:
`docker compose --env-file .env.demo -f compose.yaml -f compose.demo.yaml port seq 80`
after publishing it yourself, or read `docker compose logs api`.

**Health.** The API's runtime image is chiseled — no shell, no `curl` — so
Docker's health check re-invokes the app binary with `--healthcheck`.
`/health/live` is trivially cheap so a slow database never gets the container
killed in a loop; `/health/ready` reports whether every module's schema is
reachable. Both are exempt from the rate limiter.

**Migrations never run on API startup.** The migrator is a separate container that
runs and exits, and the API starts only once it has exited successfully. A
deployment that skips it starts an API against a schema it does not match.

**Starting over.** Use the guarded reset described in [Demo reset policy](#demo-reset-policy).
It replaces only the explicitly marked demo database and preserves TLS state.

For settings generated by an older launcher, add `WEBHOOKS_ENCRYPTION_KEY`
manually before the next startup; do not replace the database password or signing
key. New launcher runs generate the dedicated key with the other credentials.

**On "the same warehouse".** The seed fixes the simulator's *decisions*, not the
building they are made in. From an empty database the same seed gives the same
result every time; run it against a database that already has stock in it and the
totals will differ, because allocation has more to choose from. That is the
intended behaviour and not a bug in the seeding.

## Recurring jobs

The API hosts the runner; the migrator creates its `jobs` schema. `Jobs:Enabled`
(default `true`) controls dispatch and `Jobs:PollInterval` defaults to five seconds
(allowed: one second to one minute). Use double underscores in environment names,
for example `Jobs__Enabled=false`. The OpenAPI export never dispatches jobs.

Inventory reconciliation remains enabled by default, with
`Inventory:Reconciliation:Interval=01:00:00` and `InitialDelay=00:05:00`.
Intervals must be one minute to seven days, initial delays zero to seven days.
Disabling a job preserves its stored schedule. Changing an interval applies after
the next successful run; changing initial delay only affects new schedules.

Each module registers an `IBackgroundJob` implementation with `AddRecurringJob`,
a stable unique name, and its configuration section. Each execution gets a fresh
DI scope and a `BackgroundJobRun.IdempotencyKey`. Handlers that write business
state must use that key through their owning module's idempotent command seam.
The runner guarantees at-least-once delivery, not exactly-once side effects.

A dedicated PostgreSQL session lock excludes healthy API replicas per job. A
crash, cancellation, or lost acknowledgement leaves the persisted run identity
for the next attempt. A lost database connection can release that lock while a
handler is still running, so handler idempotency remains necessary. Independent
jobs may run together; the same healthy job cannot overlap itself.

Failures retain the key and retry after 30 seconds, doubling to a five-minute
cap. Successful completion schedules the next run from completion time; downtime
does not produce a burst of catch-up runs. Structured logs include job name, run
ID, scheduled time, completion, and retry due time. The `jobs.JobStates` table
records pending identity, last attempt, next due time, and failure count;
`jobs.execution_failed` is a diagnostic code, not an HTTP error. Reconciliation
remains read-only and may record another audit observation after a retry.

Dispatch tracks each in-flight job independently, so a blocked handler does not
delay other schedules. Every attempt writes a fresh concurrency token before execution;
success and failure saves reject stale attempts after lock loss, preserving a
replacement run’s identity and due time.

### Opt-in cycle counting

A supervisor edits each warehouse's schedule on the console Counting screen.
Schedules are disabled by default. Observe the counting policy before enabling
one; daily cadence and 20 tasks are the starting values. The interval is 1–10080
minutes and the budget 1–200 tasks. Enabling or changing cadence sets the next
run one interval from the save. These are elapsed intervals after completion,
not a wall-clock cron or local-midnight promise.

`GET /counting/schedules` lists active warehouses with their current or default
schedules. `PUT /counting/schedules/{warehouseId}` atomically saves `enabled`,
`intervalMinutes`, and `maxTasks`; only supervisors and administrators can save.
Responses show next due time, pending/retry state, last plan/run, and failure
code. The versioned API prefix applies as usual.

Counting publishes a scoped job catalog through SharedKernel. It discovers
configured warehouse instances of the code handler, named `counting.{warehouseId}`,
without exposing Counting tables to Jobs. The runner tracks each instance
independently, so a blocked warehouse cannot delay another warehouse's next run.
`Jobs:Enabled=false` stops all dispatch; schedule edits take effect without an
API restart. Due schedules are normally noticed within a minute.

Each warehouse persists a pending run ID before planning. Its key identifies the
plan and line keys identify tasks; lost enqueue acknowledgements resume those
same tasks. Partial enqueues and unavailable/inactive warehouses remain pending
for retry. A concurrency token rejects stale scheduler saves after lock loss. A persistent
warehouse fence rotates in the same transaction as every planning write, so a
stale policy result cannot commit duplicate open holdings after another intent
acquires the released lock.
Disabling a schedule stops future plans while an already pending plan finishes;
existing count tasks can be withdrawn through the ordinary plan action.

The handler uses the existing count policy, retaining its quiet period, exclusions,
trace, and task budget. Open holdings from manual or scheduled plans are excluded.
No due holdings is a successful empty plan with a decision trace. Plans appear
under actor `count-scheduler` (reserved system actor ID, `Guid.Empty`), without
borrowing a human account. Operators still confirm counts; Inventory computes
any variance.

Planning, schedule saves, and cancellation share a warehouse-scoped session lock.
Before cancelling, unresolved task enqueues converge under their keys so a stale
planner can only rediscover cancelled tasks afterward. Closed plans are not
re-enqueued on retry.

### Webhook configuration

Set `Webhooks__EncryptionKey` (`WEBHOOKS_ENCRYPTION_KEY` in Compose) to a persistent
base64-encoded random 32-byte key before registering production receivers. Keep
it in deployment secrets, share it across replicas, and preserve it on restart;
changing it makes existing signing secrets unreadable. The development override
has a public demo key that must never be used in production.

Public HTTPS receivers require no allowlist. For deliberately internal receivers,
set exact `Webhooks__AllowedOrigins__0` origins; these permit private addresses and
HTTP only for those origins. Development allows `http://host.docker.internal:9099`
for the local verification receiver. Network redirects and proxies are disabled,
DNS is checked at socket connection, responses are limited to 513 bytes, and a
whole request has a ten-second deadline. Exclude confidential `/webhooks/invoke/`
paths from upstream proxy access logs and tracing as the application does.

### Receipt-age projection upgrade

Stop older API writers before the first receipt-age migration, then run the
migrator before starting the new API. Migration `ReceiptAgeProjection` adds a
nullable balance age and a completion marker. Its module seeder locks balances,
streams existing ledger history once, and bulk-updates only derived age fields.
Large histories require a migration maintenance window; subsequent starts skip
the completed rebuild. A failure rolls the update and marker back together and
can be retried. Quantities and immutable ledger entries are preserved, and a
drifted quantity remains without a claimed receipt age. Rolling back the schema
drops only the derived age/marker; rebuilding them again requires the migrator.

### Blind-bin task upgrade

Stop the old API before applying `BlindBinTasks`, `BlindBinCounts` and
`BinCountProvenance`, then run
the migrator and start the matching API/frontend build. Existing item tasks and
holding-count records are retained. New bin tasks deliberately have no item and
quantity zero; older application builds cannot interpret them. Once bin audits
exist, roll forward or restore a complete pre-upgrade database backup instead of
running the old schema against those audit records. Entire-bin confirmations are
bounded to 200 holding observations and briefly serialize balance writers while
checking the movement version and committing all corrections.
