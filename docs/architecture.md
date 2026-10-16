# Architecture

A tour of the system for somebody about to change part of it. The *decisions*
behind this shape live in [the decisions](decisions.md); the rules that must survive your
change live in [invariants.md](invariants.md).

## The one-paragraph version

Countflow is a **modular monolith**: one .NET process, one PostgreSQL database,
and one schema per module with no cross-schema queries anywhere. Stock truth is
an **append-only ledger** — balances are a projection of it, not the source of
truth. Work the system directs is chosen by **rule sets held as data**, and no
decision can be returned without the trace that explains it. Two React SPAs and
a warehouse simulator are all clients of **one versioned HTTP surface**, and so
is anybody integrating from outside.

## Repository layout

```
backend/                     .NET 10 · modular monolith · vertical slices
  src/Countflow.Api          host, endpoints, auth, OpenAPI export
  src/Countflow.Migrator     runs migrations + seeders, then exits
  src/Countflow.Bootstrap    ModuleCatalog.All — the module list both hosts compose from
  src/Countflow.SharedKernel Result/Error, module plumbing, GS1 parser, rules engine
  src/Countflow.Simulator    a client of the HTTP API — no project references at all
  src/Modules/*              one PostgreSQL schema and DbContext each
  tests/                     SharedKernel unit tests + Testcontainers integration tests
frontend/                    pnpm workspace · Vite + React + TypeScript
  apps/console               admin and supervisor SPA
  apps/handheld              scanner PWA with a durable offline command queue
  packages/ui                design tokens, theme provider, Radix-based primitives
  packages/i18n              en/tr catalogues — the API never returns prose
  packages/api-client        generated from OpenAPI — never hand-edited
docker/                      Dockerfiles + the Caddy edge config
scripts/demo.sh              one-command demo stack
docs/                        this documentation and the API reference
```

## What is in it

**Backend** — .NET 10 modular monolith, `backend/`.
- `Countflow.Api` — host, endpoints, auth, OpenAPI export.
- `Countflow.Migrator` — runs migrations + seeders, exits. Never on API startup.
- `Countflow.Bootstrap` — `ModuleCatalog.All`, the single module list both hosts
  compose from.
- `Countflow.SharedKernel` — `Result`/`Error`, module plumbing, **GS1 parser**
  and `Gtin`.
- `Modules/Identity` — users, roles, JWT with refresh-token rotation and reuse
  detection, plus **service accounts and their API keys**, behind a
  second authentication scheme chosen by a policy-scheme forwarder. Both
  credentials produce the same principal, so there is one authorization
  vocabulary rather than two.
- `Modules/MasterData` — warehouse topology, items, pack configurations,
  business partners, scan resolution, CSV import. Publishes `IItemDirectory` and
  `ILocationDirectory` (contracts live in `SharedKernel/Contracts`).
- `Modules/Inventory` — the append-only stock ledger, the balance projection,
  lots, licence plates, replay, the drift reconciler and — beside the ledger
  rather than inside it — **stock reservations** and **stock counts**. Publishes
  `IStockPosting`, `IStockQuery`, `IStockAvailability`, `IStockReservations`,
  `IStockCounts`, `IStockChangeFeed`, and `IStockHistory`, which folds the ledger forward
  to hand out the warehouse as it stood at any past instant.
  The stranded-reservation report and owner-scoped lookup share one Inventory
  query; see [the outbound invariants](invariants.md#the-outbound-loop).
- `Modules/Rules` — the versioned rule sets, the putaway (single-item and
  whole-pallet), allocation, cycle-count and disposition vocabularies and the
  persisted decision traces. Publishes `IPutawayAdvisor`, `IPalletPutawayAdvisor`,
  `IAllocationAdvisor`, `ICountPolicy`, `IDispositionAdvisor`,
  `IDecisionTraceReader` and `IShadowReplay`. The rule-set **editor**
  lives here too: drafts, clone-from-active, and promotion.
- `Modules/Work` — pick tours and the task queue an operator's handheld reads from, and the
  confirmation that turns a job into a ledger entry. Putaways and picks are the
  same kind of thing to it. Publishes `IWorkQueue` and `IPutawayReceipts`; the
  latter reserves and acknowledges the receipt-to-task handoff before a growing
  pallet can be claimed. See [decision 27](decisions.md#27-a-receipt-reserves-its-handoff-before-it-posts-stock).
- `Modules/Inbound` — advice notes and the dock receipt. Publishes nothing: its
  output is stock and work, and both are already somebody else's surface.
- `Modules/Outbound` — sales orders, waves, and the release that commits stock
  and raises the picks. Publishes nothing, for the same reason.
- `Modules/Counting` — count plans, opt-in warehouse schedules, and the record of which holdings are already
  on somebody's list. Publishes nothing: its output is work and, through that
  work, counts, and both are already somebody else's surface.
- `Modules/Replay` — replay runs and what each comparison found. Publishes
  nothing: its output is an argument, and arguments are read by people.
- `Modules/Webhooks` — encrypted receiver subscriptions, immutable signed ledger
  batches, and hashed reconciliation capability URLs. Publishes no stock writes;
  consumes the Inventory feed contract and the registered scoped job handler.
- `Modules/Replenishment` — fixed pick-face min/max policies and durable bulk-to-face
  plans. Uses Inventory reservations, Rules allocation traces, and ordinary Work
  tasks through published contracts; per-face handlers are discovered by Jobs.
- `Modules/Jobs` — durable recurring schedules and unfinished run identities.
  Modules register scoped handlers through `IBackgroundJob`; Jobs owns no
  warehouse business data.
- `Modules/Labels` — label templates, the ZPL renderer and the record of what was
  printed. Publishes nothing yet: it is a reader of four other modules and its
  output is a piece of paper. The seam where data stops and code starts is
  `LabelVocabulary` — a **kind** is a binding contract in code, a **template** is
  data ([decision 13](decisions.md#13-a-label-kind-is-code-a-label-template-is-data)).
  Transport is deliberately elsewhere, which is to say nowhere yet.
- `Countflow.Simulator` — a console project that runs a warehouse forwards by
  **driving the HTTP API** ([decision 6](decisions.md#6-the-simulator-is-a-client-not-a-module)). References nothing, so it cannot cross a
  module boundary, and every state it reaches is one a real operator could have
  reached.
- `SharedKernel/Gs1` — GS1 identifiers in both directions: `Gs1Parser` reads what a
  scanner sends, `Gs1ElementString` builds what a symbol has to say. The second is
  the more dangerous of the two — a parser that errs rejects a scan, a builder that
  errs prints two thousand unreadable pallets — which is why FNC1 placement lives
  there and nowhere else.
- `SharedKernel/Rules` — the pure rules engine ([decision 4](decisions.md#4-every-decision-carries-its-reasoning)), and
  `RuleInfluence`, which reads a ranking back and says which rules produced it.
  Stateless and synchronous, so replay runs it over history with no database in
  the loop.

**Frontend** — pnpm workspace, `frontend/`.
- `apps/console` — admin/supervisor SPA (TanStack Router + Query).
- `apps/handheld` — genuinely offline-capable PWA for scanners.
  `src/lib/outbox/` is a durable IndexedDB command queue every write goes through,
  and `src/screens/QueueScreen.tsx` is the reconciliation screen for what the
  warehouse refused while the device was away.
- `packages/ui` — tokens, theme provider, Radix-based primitives, `ScanField`.
- `packages/i18n` — en/tr catalogues, formatting helpers, typed cardinal translation
  through i18next's locale rules, and numeric-string selection for coded rule notes.
- `packages/api-client` — **generated** from OpenAPI. Never hand-edit.

**Endpoints — 58 paths, all under `/api/v1`.** Auth
login/refresh/logout/me · auth service-accounts (list, create) ·
service-accounts/{id}/keys (list, create) · service-accounts/keys/{id}/revoke ·
inventory changes · warehouses · locations ·
items · items/{sku} · scan/resolve · import/items · import/locations ·
inventory receipts/transfers/adjustments/status-changes/scrap ·
inventory movements/{id}/reversal · inventory balances · inventory movements ·
inventory reconciliation + reconciliation/history · inventory accuracy ·
inventory reservations/stranded ·
rules sets/traces/putaway-preview/allocation-preview/cycle-count-preview ·
work tasks/tasks{id}/claim-next/claim/release/complete · work statistics ·
inbound asns (list, get, create) · inbound receipts ·
outbound orders (list, get, create) · orders/{id}/release/pack/despatch/cancel ·
outbound waves (list, create) · waves/{id}/release ·
counting plans (list, get, create) · plans/{id}/cancel ·
rules sets (create, replace, promote, archive) ·
replay runs (list, get, create).

The worked reference for all of it is [docs/api/](api/).

**Seed:** 2,245 locations, 500 SKUs, 1,836 pack levels, 11 partners, active
putaway, allocation and cycle-count rule sets, three advice notes and three
sales orders. The ledger, the reservations, the counts, the count plans and the
work queue seed nothing — all of them start empty by design.

---

## How a request travels

A dock receipt is the shortest path through every layer worth knowing about:

```
POST /api/v1/inbound/receipts          Idempotency-Key: <client-generated>
  │
  ├─ MapCountflowModules mounts every module under ApiSurface.Prefix, so the
  │  endpoint itself is written as MapPost("/inbound/receipts") and cannot
  │  land outside the published version.
  │
  ├─ Inbound/Features/RecordReceipt/   endpoint + command + handler + validator,
  │                                    together, as a vertical slice
  │
  ├─ IStockPosting.ReceiveAsync(…)     the published contract — Inbound has no
  │                                    project reference to Inventory
  │      └─ StockLedger.PostBatchAsync locks every balance row it will touch in
  │         one global order, appends the movements, folds them into the
  │         projection, all in one transaction
  │
  ├─ IPutawayAdvisor.SuggestAsync(…)   RuleEngine.Evaluate returns a Decision<T>
  │                                    that *is* the trace; there is no way to
  │                                    get a result without one
  │
  └─ IWorkQueue.RaiseAsync(…)          the putaway task, carrying the trace id
```

Failures come back as `Result`/`Result<T>` carrying a coded `Error`, which
`ToHttpResult()` turns into `application/problem+json` with a stable `code`.
Clients branch on the code and render their own wording — the API returns no
prose, which is what makes the Turkish handheld a catalogue rather than a
backend change.

## The boundaries, and what enforces them

| Boundary | Enforced by |
|---|---|
| A module never reads another's tables | No project reference: consumers see only the interface in `SharedKernel/Contracts` — a violation is a compile error |
| One posting path into the ledger | `IStockPosting` publishes *operations* (`ReceiveAsync`, `PickAsync`, `CountAsync`), never `PostAsync(movementType, …)` |
| The ledger is append-only | Init-only properties, a `SaveChangesAsync` override, and a statement-level PostgreSQL trigger rejecting `UPDATE`/`DELETE`/`TRUNCATE` |
| No decision without a trace | `Decision<T>`'s constructor is `internal` to the engine |
| No unexplained directed movement | `PutawayInstruction.DecisionTraceId` is a `required Guid`, not a `Guid?` |
| The published surface never drifts | CI regenerates the OpenAPI document and the typed client, and fails on a diff |
| Adding a module cannot break a host | One line in `ModuleCatalog.All`; the API and migrator both compose from it |

## Where to find each of the three pillars

The [README](../README.md#why-another-wms) argues what they are and shows what
they produce. This is where the code is.

| Pillar | Start here | Then |
|---|---|---|
| **Provable** | `Modules/Inventory` — `StockLedger` (the one posting path), `StockMovement`, `StockBalance` | `LedgerReplay` (one `UNION ALL` serving both reconciliation and point-in-time enquiry), `LedgerReconciler` (reports drift, never repairs it) · [decision 1](decisions.md#1-inventory-truth-is-an-append-only-ledger) |
| **Explainable** | `SharedKernel/Rules` — `RuleEngine`, `Decision<T>`, `RuleInfluence` | `Modules/Rules` for the three vocabularies and the persisted traces · [decision 4](decisions.md#4-every-decision-carries-its-reasoning) |
| **Testable** | `IStockHistory` — folds the ledger forward to any past instant | `IShadowReplay` re-runs both rule sets over that reconstruction; `Modules/Replay` is the experiment log |

## Data model notes worth knowing early

- **Every identifier is a UUIDv7**, generated by the client. Sortable, and
  therefore never safe to truncate for uniqueness.
- **Idempotency keys are unique indexes**, not conventions, and derived keys
  chain (a wave's begets an order's, which begets a line's). Some columns hold
  256 characters for that reason.
- **`NULLS NOT DISTINCT` is load-bearing** on the balance key: without it a bin
  holding a non-lot-tracked item grows a fresh balance row on every posting.
- **Partial unique indexes** enforce "one active rule set per decision kind per
  warehouse" and "one open putaway per licence plate", because two concurrent
  writers would otherwise both find nothing and both insert.

## Frontend shape

Two SPAs, one toolchain, opposing constraints — the console is data-dense and
always online; the handheld must stay small and keep working through a dead
aisle. They share `packages/ui` (tokens and Radix-based primitives),
`packages/i18n` (en/tr, with `tr.ts` typed against `en.ts` so a missing key
fails the build) and `packages/api-client` (generated).

The handheld's distinctive part is `src/lib/outbox/` — a durable IndexedDB queue
that **every** write goes through, online or not, plus a reconciliation screen
for what the warehouse refused while the device was away.
[decision 8](decisions.md#8-the-handheld-writes-to-a-durable-queue).

## Deployment

`docker compose up` brings up PostgreSQL, a one-shot **migrator** container, the
API, Caddy serving both SPAs and proxying `/api`, and Seq for logs. Migrations
never run on API startup: two replicas booting together would race, and EF's
history table is not a distributed lock. The API runtime image is **chiseled** —
no shell, no package manager, non-root — so the Docker health check re-invokes
the app binary with `--healthcheck` rather than shipping `curl`.

### Order orchestration sessions

Order mutations serialize through an advisory lease. `ModuleSession` pins scoped
module contexts to its physical connection; module transactions remain separate
and sequential. Session loss stops the request rather than reconnecting without
the lock. See [decision 25](decisions.md#25-a-repair-replaces-a-promise-and-its-writes-keep-the-lock-session)
and [the outbound invariants](invariants.md#the-outbound-loop).
