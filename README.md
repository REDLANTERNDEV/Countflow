<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)"
            srcset="docs/assets/logo-horizontal-on-dark.svg">
    <img src="docs/assets/logo-horizontal.svg" alt="Countflow" width="340">
  </picture>
</p>

<p align="center">
  <strong>A warehouse management system that can prove what happened, explain why
  it happened, and let you test a change before you make it.</strong>
</p>

<p align="center">
  <a href="https://github.com/REDLANTERNDEV/Countflow/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/REDLANTERNDEV/Countflow/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="Licence: GPL-3.0" src="https://img.shields.io/badge/licence-GPL--3.0-blue.svg"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="React 19 + Vite" src="https://img.shields.io/badge/React-19%20%2B%20Vite-61DAFB">
  <img alt="PostgreSQL 17" src="https://img.shields.io/badge/PostgreSQL-17-336791">
</p>

<p align="center">
  <a href="CHANGELOG.md">What's in it</a> ·
  <a href="docs/history.md">How it was built</a> ·
  <a href="ROADMAP.md">What's next</a> ·
  <a href="docs/api/">Integrating</a> ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

Countflow is a WMS for a single-company distribution centre — the warehouse
module of an ERP, built standalone and integration-ready. It is an open-source
Tier 2 system: real directed work, real inventory accuracy, real integrations,
without the six-figure implementation.

The warehouse loop runs end to end: goods are booked in at the dock, a versioned
rule set chooses where they go and records why, the operator confirms on a
scanner that keeps working through a dead zone, orders are allocated by a second
rule set, picked, packed and despatched — and the ledger sums to zero from
supplier to customer, with the reasoning attached to every directed movement in
between.

---

## Why another WMS

An ERP knows *that you own 500 units*. A WMS knows *which 500 units, in which
bins, on which pallets, from which lot, and who touched them last* — and tells
workers what to physically do next.

Below Tier 1 that second part is usually thin: stock lives in a mutable
`quantity` column, the decision logic is a black box, and changing a putaway
rule in a live warehouse is frightening enough that bad rules survive for years.
Countflow is built around three pillars that all fall out of one architectural
decision — an immutable ledger.

### 1. Provable — the double-entry stock ledger

Every stock change is an append-only movement between two locations, borrowed
straight from accounting. Nothing is created or destroyed, only moved:

```
Receive    SUPPLIER      → DOCK-01
Putaway    DOCK-01       → A-07-03-02
Pick       PICK-B-12     → CART-004
Ship       PACK-STN-02   → CUSTOMER
Count loss A-07-03-02    → ADJUSTMENT
```

Balances are a projection you can rebuild from scratch and reconcile, so the
contents of any bin at any past timestamp can be reconstructed exactly.
See [decision 1](docs/decisions.md#1-inventory-truth-is-an-append-only-ledger).

### 2. Explainable — every decision carries its reasoning

Bin selection and lot allocation each emit a structured trace: the candidates
considered, the rules applied, why each loser was rejected, and why the winner
won — surfaced to the supervisor *and* to the operator holding the scanner. Wave
grouping and task assignment are named in the same vocabulary and get their rule
sets later; nothing about the mechanism changes when they do.

Real output from the seeded warehouse, lightly formatted — 2,240 locations
considered, 1,920 of them admissible:

```
Putaway for SKU-10001 ×24 → WH1-STOR-AMB-A01-01-02-A     rule set "Standard putaway" v1
  chose  WH1-STOR-AMB-A01-01-02-A                        score 0.598
    +0.122  location is empty
    +0.250  C-class item in a reserve location
    +0.225  zone AMB, 10% along the route
    +0.001  this would fill 1% of the free space
  ruled out
    −  WH1-RECV-DOCK-DOOR-00-00-D01   the goods are already standing here
    −  WH1-RECV-DOCK-DOOR-00-00-D02   not a storage location — it is a dock
    −  WH1-STOR-CHL-A01-01-01-A       Ambient goods cannot go in a Chilled zone
```

The same engine, the same `Decision<T>` and the same journal answer the opposite
question on the way out — which stock should satisfy this order line. Real output
again, three admissible holdings trimmed to two:

```
Allocation for SKU-10003 ×100                    rule set "Standard allocation" v1
  #1  WH1-STOR-AMB-A01-01-01-B / L-NEW                             score 0.799
    +0.249  expires 2027-06-01, in 294 days
    +0.250  in a pick-face location
    +0.200  this would clear the last 60 here
    +0.100  zone AMB, 0% along the route
  #2  WH1-STOR-AMB-A01-02-01-A / L-MID                             score 0.741
    +0.291  expires 2027-04-01, in 233 days
    …
    +0.000  zone AMB, 100% along the route
  ruled out
    −  …A01-01-01-A / L-OLD   only 8% of its life left, and the customer
                              requires 33% (expires 2026-10-01)

  take 60 from A01-01-01-B / L-NEW      take 40 from A01-02-01-A / L-MID
```

Three things are visible there that a stock report cannot show you. The
short-dated batch is **not expired** and is perfectly sellable — it is simply not
sellable to *this* customer, under a contract term most systems cannot express and
none explains at the point of use. One line drew on **two batches**, out of one
decision, so "why these two?" has a single answer. And the older batch lost to the
nearer one by four hundredths, because sixty days apart on a two-year shelf life is
not worth walking the building for — on a chilled item with a three-week life the
same gap would have won outright.

The API sends codes and arguments, never sentences; the wording above comes from
`packages/i18n`, which is why the same trace reads in Turkish on a Turkish
handheld.

When nobody can explain why the system sent someone to a strange bin,
supervisors start overriding it — and once they override, accuracy collapses.
Explainability is an accuracy feature, not a UI nicety.

*Honest note:* Oracle WMS ships a rules-engine trace, so this is
industry-validated rather than invented. The difference is placement — it is a
Tier 1 diagnostic aimed at consultants, and here it is a first-class, always-on
surface in an open system.

### 3. Testable — shadow replay of rule changes

Because the ledger is a complete history, the warehouse as it stood at any past
instant can be rebuilt — so a candidate rule set can be run against the decisions
that were really made, and the differences read off, before anybody promotes it.

Real output, replaying the shipped rule sets against a simulated warehouse:

```
RPL-2026-0011  Putaway  Standard putaway v1  against Standard putaway v1
  121 replayed · 121 reproduced exactly · 0 changed · 893 ms

  rule                             weight    asked   realised   spread
  putaway.consolidate_same_item      0.35    35.0%      55.1%    0.586
  putaway.velocity_slotting          0.25    25.0%      22.1%    0.410
  putaway.travel_distance            0.25    25.0%      15.5%    0.192
  putaway.cube_utilisation           0.15    15.0%       7.4%    0.142
```

Two things there are worth more than the diff. **Fidelity is measured, not
asserted**: replay re-runs the rules that made each decision and reports how many
it reproduced exactly, so a reconstruction that has quietly drifted says so
instead of producing confident nonsense. And *realised* versus *asked* is what a
rule was actually **worth** to the ranking, as opposed to what its weight
claimed — five scoring bugs in this codebase were a scale that could not express
a difference that existed, and the last two were found by that column rather than
by a person.

### 4. And it keeps working when the network does not

Warehouses have dead aisles. The handheld is a PWA whose **every** write — online
or not — goes through a durable IndexedDB queue keyed by the idempotency key it
was born with, so a confirmation survives a battery change and lands exactly once
when the signal comes back.

The interesting half is what happens when the warehouse has moved on in the
meantime. A refusal forty minutes later is not an error, it is news, and the
device is the only thing that still knows what the operator did:

```
Needs you
  Cycle count   36 × SKU-10000   counted 34   at A01-02-03-A   scanned 18:20

  The warehouse says   Somebody else confirmed this job before yours reached us.
  What to do           The goods may have been moved twice. Check A01-02-03-A,
                       and tell your supervisor what you find.
```

Nothing is ever shown as saved when it is only saved locally, and a pending
command cannot be deleted from the device at all — it is the record of a pallet
somebody moved. [decision 8](docs/decisions.md#8-the-handheld-writes-to-a-durable-queue).

---

## Running it

The only prerequisite is Docker.

```bash
docker compose up
```

That brings up PostgreSQL, applies every module's migrations, seeds a demo
warehouse and users, and serves both frontends:

| | |
|---|---|
| Console | http://localhost:8000 |
| Handheld | http://localhost:8000/handheld/ |
| API | http://localhost:8080 |
| API reference (Scalar) | http://localhost:8080/scalar/v1 |
| Logs (Seq) | http://localhost:5341 |

The seed builds a realistic DC rather than a toy one: **2,245 locations** across
30 aisles of ambient and chilled racking, dock doors, pack benches, quarantine
and the five virtual ledger counterparties, plus **500 SKUs** with ABC
distribution, mixed lot/expiry/serial tracking and full packaging hierarchies
whose GTINs carry correct check digits. Rule evaluation and pick routing only
behave interestingly at that scale.

Worth a look first: **Scan test** in the console decodes a GS1-128 label and
resolves it against the catalogue — item, packaging level, batch, expiry, and
the conversion to base units, with warnings when a lot-tracked item arrives
without a batch or the stock is already expired.

Then **Stock** and **Ledger**. The ledger starts empty — post a receipt against
`/api/v1/inventory/receipts` and watch both sides of the entry appear: the stock
in the bin, and the matching negative on the supplier counterparty. Set a date on
the Stock screen to rebuild balances as they stood at that instant, and press
*Verify against the ledger* to replay every entry and prove the balances match.

Or skip the typing and run the warehouse forwards. The simulator is a **client**
of the API — it holds no project references and reaches the system only over
HTTP — so everything it produces is a state a real operator could have reached:

```bash
export COUNTFLOW_SUPERVISOR_SECRET=Countflow#2026 \
       COUNTFLOW_OPERATOR_SECRET=Countflow#2026 \
       COUNTFLOW_ADMIN_SECRET=Countflow#2026
cd backend && dotnet run --project src/Countflow.Simulator -- --days 4
```

Four simulated days of receipts, putaways, orders, picks, despatches and counts,
with a decision trace behind every directed movement. Then the **Replay** screen
has something to argue about.

Demo sign-ins (development only — seeding is off by default):

| User | Operator code | Role |
|---|---|---|
| `admin` | `ADM001` | Admin |
| `supervisor` | `SUP001` | Supervisor |
| `operator` | `OP001` | Operator |

Password for all three: `Countflow#2026`.

### Working on it

```bash
cd backend  && dotnet build && dotnet test     # needs Docker: real PostgreSQL
cd frontend && pnpm install && pnpm dev        # HMR against the API on :8080
```

Full setup, every command, and the review bar: [CONTRIBUTING.md](CONTRIBUTING.md).

One rule worth knowing before you start — after changing any endpoint, run
`pnpm export:openapi && pnpm generate:api`. CI runs the same pair and fails on a
diff, so the published OpenAPI document can never drift from what the server
actually serves.

---

## Integrating with it

There is **one API and no private sibling.** The console above, the handheld on
the floor and the simulator are all clients of exactly the same 58 versioned
paths, which is a constraint on us and a guarantee for anybody building against
it — the argument is in [decision 7](docs/decisions.md#7-the-public-surface-is-the-surface).

```bash
curl -s -H "X-Api-Key: cfk_…" https://your-host/api/v1/auth/me
```

An integration is a **service account** holding an API key, and it is authorised
by the same roles a person holds — there is no scope vocabulary to learn, because
two sets of rules about one question is one set that goes out of date. To keep an
external system in step, walk the ledger forwards from a cursor:

```bash
curl -s -H "X-Api-Key: cfk_…" \
  "https://your-host/api/v1/inventory/changes?afterSequence=4468&limit=200"
```

Every change inventory has ever undergone, in order, exactly once, with no outbox
and no second copy of anything — `StockMovement.Sequence` was already a total
order, so the feed is the ledger read in a different direction.

**[docs/api/](docs/api/)** has the rest: authentication, the compatibility policy,
idempotency, pagination and the change feed, an error-code table, and both
warehouse loops end to end with worked requests.

## A demo somebody can be pointed at

```bash
./scripts/demo.sh
```

Generates its own secrets, brings the stack up, and then **builds the warehouse
with a container that drives the public API**. It is not seeded with plausible
history; it is run forwards by a client, so every state a visitor sees is one the
API would have let them reach themselves.
[decision 6](docs/decisions.md#6-the-simulator-is-a-client-not-a-module), made literal.

The login screen of a demo build lists its accounts, and the **Tour** link
explains what each screen is for. Pointing it at a domain is four steps in
[docs/api/hosting.md](docs/api/hosting.md); Caddy handles the certificate.

---

## Architecture

```
backend/                     .NET 10 · modular monolith · vertical slices
  src/Countflow.Api          host, endpoints, auth, OpenAPI
  src/Countflow.Migrator     runs migrations + seeders, then exits
  src/Countflow.Bootstrap    the single module catalog both hosts compose from
  src/Countflow.SharedKernel Result, module plumbing, GS1 parser, rules engine
  src/Modules/*              one PostgreSQL schema and DbContext each
frontend/                    pnpm workspace · Vite + React + TypeScript
  apps/console               admin and supervisor SPA
  apps/handheld              offline-first PWA for scanners — every write goes
                             through a durable IndexedDB queue ([decision 8](docs/decisions.md#8-the-handheld-writes-to-a-durable-queue))
  packages/api-client        generated from OpenAPI — never hand-edited
docker/                      Dockerfiles + Caddy edge config
docs/                        documentation and the API reference
```

The full tour — what each module owns, how a request travels, and what enforces
the boundaries — is [docs/architecture.md](docs/architecture.md).

Key decisions and their reasoning ([all of them](docs/decisions.md)):

- [decision 1](docs/decisions.md#1-inventory-truth-is-an-append-only-ledger)
- [decision 2](docs/decisions.md#2-a-modular-monolith-one-schema-per-module)
- [decision 3](docs/decisions.md#3-two-react-spas-not-blazor-and-not-nextjs)
- [decision 4](docs/decisions.md#4-every-decision-carries-its-reasoning)
- [decision 5](docs/decisions.md#5-shadcn-for-behaviour-our-own-visual-layer)
- [decision 6](docs/decisions.md#6-the-simulator-is-a-client-not-a-module)
- [decision 7](docs/decisions.md#7-the-public-surface-is-the-surface)
- [decision 8](docs/decisions.md#8-the-handheld-writes-to-a-durable-queue)

Two details worth calling out, because both are easy to get wrong and expensive
to retrofit:

- **Migrations run in a one-shot container, never on API startup.** Two API
  replicas booting together would otherwise race on the same migration, and EF's
  history table is not a distributed lock.
- **The API image is chiseled** — no shell, no package manager, non-root. Docker's
  health check re-invokes the app binary in probe mode rather than giving up the
  hardened base image for a container with `curl` in it.

---

## Standards

Countflow targets the interop standards a real WMS is expected to speak:
**GTIN** (GS1 AI `01`), **SSCC** (`00`) as the license-plate format, **GS1-128**
with Application Identifiers parsed into item + lot + expiry + quantity, and
**EDI 856/940/945** for trading-partner exchange.

## Scope

Countflow is a WMS for a **single-company distribution centre** — the warehouse
module of an ERP, built standalone and integration-ready.

Deliberately out of scope: labour management, slotting optimisation, 3PL billing,
robotics/WCS integration, yard and dock scheduling, carrier rate shopping, and
multi-tenant hosting. [ROADMAP.md](ROADMAP.md) says what is open and why it was
left, including the things that were refused on their merits.

## Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) covers
setup, the commands, the review bar, and
[how this project verifies things](CONTRIBUTING.md#how-we-verify) — which is not
"the tests pass".

Two files carry most of the knowledge: [docs/invariants.md](docs/invariants.md),
the rules that must survive your change, and [docs/gotchas.md](docs/gotchas.md),
the traps that will otherwise cost you an afternoon.

Security reports: see [SECURITY.md](SECURITY.md) — please do not open a public
issue.

## Licence

[GPL-3.0](LICENSE)
