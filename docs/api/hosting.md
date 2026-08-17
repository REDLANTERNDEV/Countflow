# Hosting: the demo stack, and pointing it at a domain

## One command

```bash
./scripts/demo.sh
```

On first run it generates `.env.demo` — a Postgres password, a JWT signing key, a
Seq password and three API keys, all from `openssl rand` — and then brings the
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
| `postgres` | One database, nine schemas, one per module. Not published. |
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

**Starting over.**

```bash
docker compose --env-file .env.demo -f compose.yaml -f compose.demo.yaml down -v
```

Drops the demo's volumes — and only the demo's, because it is its own project.
That includes `caddy-data`, so you will re-issue certificates on the next run;
mind Let's Encrypt's rate limits if you do it often. Keep `.env.demo` and the next
run rebuilds the same warehouse.

**On "the same warehouse".** The seed fixes the simulator's *decisions*, not the
building they are made in. From an empty database the same seed gives the same
result every time; run it against a database that already has stock in it and the
totals will differ, because allocation has more to choose from. That is the
intended behaviour and not a bug in the seeding.
