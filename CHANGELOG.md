# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The compatibility
promise for the HTTP surface is stated more precisely in
[docs/api/versioning.md](docs/api/versioning.md); the reasoning behind the
features is in [docs/history.md](docs/history.md).

## [Unreleased]

Nothing yet. See [ROADMAP.md](ROADMAP.md) for what is deliberately open.

## [1.0.0] — 2026-08-12

First complete release. The warehouse loop runs end to end, every directed
movement carries the reasoning that produced it, and the surface an integrator
uses is the only surface there is.

### Added

**Inventory — the provable half**

- Append-only double-entry stock ledger. Every change is a movement between two
  locations; nothing is ever updated or deleted and corrections are compensating
  entries. Guarded by init-only properties, a `SaveChangesAsync` override and a
  PostgreSQL trigger that rejects `UPDATE`/`DELETE`/`TRUNCATE`.
- Balance projection written in the same transaction as its movement, with
  point-in-time reconstruction and a reconciler that replays the whole ledger and
  reports drift without ever repairing it.
- Lots, expiry, licence plates (SSCC, check-digit validated, nestable).
- Reservations beside the ledger — a claim is not a movement — with
  available-to-promise computed rather than stored.
- Cycle counts beside the ledger, where a count that agrees with the book posts
  nothing and is still recorded, because accuracy is verified over counted.
- Scrap, adjustments, and reversal as a compensating entry.

**Rules — the explainable half**

- A pure rules engine: ordered constraints, weighted preferences, and a
  `Decision<T>` that *is* the trace — its constructor is internal to the engine,
  so there is no path to a result without its reasoning.
- Three vocabularies on one engine: putaway (7 constraints, 4 preferences),
  allocation (6 and 4, including FEFO and minimum remaining shelf life), and
  cycle count (4 and 4).
- Rule sets as versioned data with `Draft` → `Active` → `Archived` promotion, a
  console editor, and validation performed by building the set through the real
  catalogue at the moment somebody saves it.
- `RuleInfluence` — what a rule was actually worth to a ranking, as opposed to
  what its weight claimed. Four scoring flaws in this project were each a single
  line of that report.

**Shadow replay — the testable half**

- `IStockHistory` folds the ledger forward to hand out the warehouse as it stood
  at any past instant, linear in history rather than quadratic.
- `IShadowReplay` re-runs both rule sets over that reconstruction rather than
  diffing two stored traces, and **measures its own fidelity instead of asserting
  it**: if the rules that made a decision no longer choose what they chose, the
  comparison says so.
- A replay experiment log, and a console screen showing the summary, the
  influence table and the per-decision diff.

**The warehouse loops**

- Inbound: advice notes, the dock receipt that posts the goods, asks the advisor
  and raises the putaway.
- Outbound: sales orders, waves, and release / pick / pack / despatch / cancel,
  with a convergent release that is always safe to run again.
- Counting: count plans raised by policy, on the same work queue as everything
  else.
- One work queue using `FOR UPDATE SKIP LOCKED`, so thirty operators pressing
  "next job" at a shift start each get a different one.

**Master data**

- Location topology (Warehouse → Area → Zone → Aisle → Bay → Level → Position)
  plus virtual ledger counterparties, items with per-packaging-level GTINs, a
  GS1-128 parser, scan resolution, and CSV import with a dry run.
- A 2,245-location / 500-SKU seed with ABC distribution, mixed lot/expiry
  tracking and full packaging hierarchies — rule evaluation only behaves
  interestingly at that scale.

**Integration surface**

- 58 versioned paths under `/api/v1`, and no private sibling: the console, the
  handheld and the simulator are clients of exactly these endpoints.
- Service accounts and API keys, authorised by the same roles a person holds —
  there is no second authorization vocabulary.
- A ledger change feed (`GET /inventory/changes`) — change data capture with no
  outbox, because the ledger was already the change stream — served behind a
  watermark so a concurrent posting can never be skipped.
- Per-credential rate limiting, partitioned on the credential presented rather
  than the identity it resolves to.
- `docs/api/`: authentication, compatibility policy, idempotency, pagination, the
  full error-code table, and both loops worked end to end.

**Clients**

- Console SPA: stock, ledger, inbound, outbound, counting, accuracy, work queue,
  rule sets with preview and drafting, replay, integrations, and a guided tour.
- Handheld PWA: receive, directed putaway and pick, cycle count, "why here?" at
  the rack, and a **durable offline command queue** every write goes through —
  with a reconciliation screen that tells an operator what they did, what the
  warehouse says, and what to do now.
- A warehouse simulator that is a client of the HTTP API and holds no project
  references, so every state it produces is one a real operator could reach.
- en/tr localisation throughout, with `tr.ts` typed against `en.ts` so a missing
  key fails the build. The API returns `{ code, args }` and never prose.

**Operations**

- `docker compose up` for the full seeded stack; `./scripts/demo.sh` for a demo
  whose warehouse is built by a container driving the public API.
- Migrations run in a one-shot migrator container, never on API startup.
- Chiseled API runtime image — no shell, no package manager, non-root — with a
  health check that re-invokes the app binary.
- CI: build with warnings as errors, integration tests against real PostgreSQL,
  an OpenAPI drift check that fails if the published spec and the server disagree,
  and container image builds.

### Fixed

Defects found by driving the running stack rather than by the test suite, kept
here because the class of bug matters more than the individual fix:

- **Five preference-normalisation flaws**, each a scale that could not express a
  difference that existed — travel measured against the wrong field, a route
  composite divided by the building's maximum, a scale not re-based to its own
  minimum. One was found by the influence report rather than by a person.
- **Uncoded transport failures**: a missing credential, a missing role and a
  mistyped path came back as bare problem documents, in a contract that tells
  integrators to branch on `code`.
- **The rule-set editor's Save path**, which had never been executed by a test
  because every test of that method asserted a refusal, and every refusal returns
  before the save.
- **A token refresh calling an unversioned path**, so every expiring session
  signed the user out instead of being refreshed, in both apps.
- **A handheld task cache that only ever grew**, advertising jobs the warehouse
  had finished with twenty minutes earlier.
- **A stalled offline command with no way back** — the automatic retry stopped, as
  designed, and nothing could then send it.
- **A reachability flag that latched**, leaving a device reporting itself offline
  on a working network.

[Unreleased]: https://github.com/REDLANTERNDEV/Countflow/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/REDLANTERNDEV/Countflow/releases/tag/v1.0.0
