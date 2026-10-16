# Why Countflow is built this way

Fourteen decisions that would be expensive to reverse, each with the argument behind
it and **what it cost**. Where one turned out to be wrong, the correction sits
next to the claim rather than instead of it.

Source comments cite these as `ADR 0001` … `ADR 0014`; the numbers are the same.

1. [Inventory truth is an append-only ledger](#1-inventory-truth-is-an-append-only-ledger)
2. [A modular monolith, one schema per module](#2-a-modular-monolith-one-schema-per-module)
3. [Two React SPAs, not Blazor and not Next.js](#3-two-react-spas-not-blazor-and-not-nextjs)
4. [Every decision carries its reasoning](#4-every-decision-carries-its-reasoning)
5. [shadcn for behaviour, our own visual layer](#5-shadcn-for-behaviour-our-own-visual-layer)
6. [The simulator is a client, not a module](#6-the-simulator-is-a-client-not-a-module)
7. [The public surface is the surface](#7-the-public-surface-is-the-surface)
8. [The handheld writes to a durable queue](#8-the-handheld-writes-to-a-durable-queue)
9. [The API publishes operations, not tables](#9-the-api-publishes-operations-not-tables)
10. [The warehouse owns quantity, the ERP owns value](#10-the-warehouse-owns-quantity-the-erp-owns-value)
11. [A wave is a sequence of decisions, not a partition](#11-a-wave-is-a-sequence-of-decisions-not-a-partition)
12. [A disposition is a verb, and returns belong to outbound](#12-a-disposition-is-a-verb-and-returns-belong-to-outbound)
13. [A label kind is code, a label template is data](#13-a-label-kind-is-code-a-label-template-is-data)
14. [Countflow issues its own licence plates](#14-countflow-issues-its-own-licence-plates)

---

## 1. Inventory truth is an append-only ledger

A WMS is only worth running if its stock figures can be trusted. The usual Tier 2
design is a mutable `stock.quantity` column updated in place, with a transaction
log written alongside for history — two sources of truth kept in step by
application code. Any bug, partial failure or concurrent update that touches one
and not the other puts them permanently out of agreement, and because the
quantity is the authoritative one, **the drift is invisible until somebody counts
the shelf**.

Countflow's central claim is that it can prove what happened. That cannot rest on
a design where the proof is a side effect.

**So every stock change is an immutable movement from one location to another,**
borrowed from double-entry bookkeeping. Stock is never created or destroyed, only
moved: receiving is a movement from the virtual location `SUPPLIER`, despatch is
a movement to `CUSTOMER`, a count loss moves to `ADJUSTMENT`. Virtual locations
are ordinary rows, which is what lets one mechanism cover every case. Status
changes — quarantine, damage, hold — are movements too, same location, different
status, so they are as auditable as physical moves. Corrections are compensating
entries; nothing is ever updated or deleted.

Current balances are a **projection**, written in the same transaction as the
movement and fully rebuildable by replaying the ledger, with a background
reconciler that replays and alarms on divergence.

In the code the invariant is guarded three deep, because a rule this important
should not depend on everybody remembering it: init-only properties, a
`SaveChangesAsync` override that refuses to persist a modified or deleted entry,
and a statement-level PostgreSQL trigger that rejects `UPDATE`, `DELETE` and
`TRUNCATE` outright.

**What it costs**

- More rows, and reads that go through a projection rather than a column.
- No "just fix the number" — a correction is a posting, which is slower to write
  and impossible to do by accident.
- Two things had to be built *beside* the ledger rather than inside it, each for
  the same reason. A **reservation** asserts nothing physical — the pallet has
  not moved and a stocktake would find exactly what it found before — so
  recording one as a movement would put a claim about the future into an
  append-only record of the past. A **count** that finds what was expected
  asserts that nothing happened, and posts no entry at all. Both live in the
  `inventory` schema anyway, because a pick must move the goods and discharge the
  promise in one transaction, and a variance must write the finding and post the
  correction in one — and two schemas cannot share a transaction.

**What this bought, twice over:** the contents of any bin at any past instant can
be reconstructed exactly, and the ledger doubles as the replay corpus that makes
[decision 4](#4-every-decision-carries-its-reasoning) possible at all. Two of the
three product pillars depend on this one.

### The correction: reservations are replayable after all

This decision said for a long time that reservations were the one input a replay
could not reconstruct, because they are current state rather than append-only
history. Writing that caveat into the replay API is what exposed it as wrong.

A reservation carries `CreatedAt` and a **set-once** `ClosedAt`, its quantity is
immutable, and the row is never deleted — a released or picked claim is marked,
not removed. So the claims standing at any past instant are exactly
`CreatedAt <= t AND (ClosedAt IS NULL OR ClosedAt > t)`, and an allocation replay
is as exact as a putaway one.

The mistake was treating "not append-only" as equivalent to "not
reconstructible". A mutable row recovers its own history perfectly well when the
mutation is monotone and timestamped. It is left recorded here because a
plausible claim surviving three rounds of review unexamined is the transferable
part — and it is why replay now **measures** its fidelity rather than asserting
it.

---

## 2. A modular monolith, one schema per module

Countflow spans several bounded contexts — master data, inventory, inbound,
outbound, work, counting, rules. The modern reflex is a service per context.

Warehouse operations argue against it. Allocating stock to an order line touches
outbound (the order), inventory (the balance and the ledger) and work (the pick
task), and it must be atomic: **a pick task that exists for stock that was never
reserved is a physical error on the floor, not a stale cache.** Distributing that
transaction buys nothing at the scale one distribution centre operates at, and
costs sagas, compensations and eventual consistency in the one place the business
cannot tolerate it.

**So: one deployable API process, modules as class libraries listed explicitly in
`ModuleCatalog`.** Each module owns exactly one PostgreSQL schema, its own
`DbContext` and its own migrations-history table inside that schema. Cross-module
access goes through a published C# interface — never another module's tables.
Inside a module, code is organised by feature slice (`Features/ReceiveGoods/`
holds the endpoint, command, handler and validator together) rather than by
technical layer.

PostgreSQL over SQL Server: cross-platform, free, and it has the concurrency
primitives allocation needs (`SELECT … FOR UPDATE`, `SKIP LOCKED`, advisory
locks). Modules are registered explicitly rather than discovered by assembly
scanning — faster startup, greppable, and the dependency direction stays obvious.

**What it costs**

- One deployment unit, so modules cannot scale independently.
- An upgrade lands everywhere at once, because package versions are managed
  centrally.
- The boundary is enforced by the compiler only where a contract exists; a
  determined developer can still take a shortcut inside a module.

What makes the boundary hold in practice is that the contracts live in
`SharedKernel/Contracts`, so a consumer references the *interface* without
referencing the implementation — a violation is a compile error rather than
something review has to catch. And schema separation makes the rest visible: a
module reaching into another's tables is a schema-qualified query somebody can
grep for. Extraction stays possible too — a module with its own schema and a
published interface is exactly the unit you would lift out.

---

## 3. Two React SPAs, not Blazor and not Next.js

Two separate SPAs, both Vite + React + TypeScript, sharing a component package
and a generated API client.

**Not Blazor.** Blazor WASM ships a multi-megabyte .NET runtime and cold-starts
slowly on exactly the low-end hardware this has to run on. Its offline story also
means bridging to JavaScript for service workers and IndexedDB, so the
"one language" benefit evaporates at the point it matters most.

**Not Next.js.** Three reasons, in increasing weight: the handheld needs Vite
regardless, because the App Router fights service workers and offline shells
while `vite-plugin-pwa` makes them trivial; the console is an authenticated
internal tool with no SEO and nothing to server-render, so Next.js's actual value
sits entirely behind a login wall; and decisively, Next.js in front of .NET means
running a Node server in production whose only job is rendering — two runtimes and
two deploy targets duplicating a backend that already exists. Vite ships static
bundles Caddy serves directly.

**Two apps, one toolchain**, because they have opposing constraints: the console
is data-dense and always online, the handheld must stay small and start in a dead
aisle. Bundling the console's tables and rule editor into the handheld defeats its
purpose; sharing the toolchain and `packages/ui` makes the split nearly free.

**What it costs**

- No shared types across the wire. Recovered by generating the TypeScript client
  from the API's own OpenAPI document, regenerated in CI and failing on drift —
  which turns the loss into a standing proof that the published spec is accurate.
- Two build configurations to keep aligned. A pnpm catalog pins shared dependency
  versions in one place to stop them diverging.

**A stale number, kept with its correction.** This decision was originally
recorded with "handheld production bundle is ~64 KB gzipped", and that figure was
quoted as current long after it stopped being true: the design system took it to
~98 KB, the screens that followed to 126.2 KB, and the offline queue to
**132.3 KB**, measured on the shipped build. The argument it was making — that a
scanner-class device cold-starts in about a second — still holds at twice the
size, and now holds with no network at all. It is flagged rather than edited
because a decision record that quotes a stale measurement as current is how a
claim outlives its evidence.

---

## 4. Every decision carries its reasoning

A WMS decides on the operator's behalf continuously: which bin to put a pallet
in, which lot to allocate, which bay to count. In most systems that reasoning is
not visible at the point of use, and two expensive failures follow.

**Supervisors stop trusting the system.** When the WMS sends someone to a bin
that looks wrong and nobody can say why, the supervisor overrides it — and once
overrides are routine, directed work degrades and inventory accuracy goes with
it. Explainability is an accuracy feature, not a UI nicety. **And rule changes
become too frightening to make**, so demonstrably bad rules survive for years.

**So decisions are traced by construction.** The engine returns a `Decision<T>`
carrying the winner, every candidate considered, the constraint that excluded
each loser and the scored breakdown behind each survivor. There is no API that
yields a result without a trace, and that is enforced in the type rather than by
convention: `Decision<T>`'s constructor is `internal`, so `RuleEngine.Evaluate`
is the only thing in the system that can produce one. Explainability cannot rot
as rules accumulate, because there is no cheaper path to route around.

Rules are declarative — ordered filters (hard constraints) plus weighted scorers
(soft preferences) — and a **rule set** is versioned, promotable data rather than
code. Traces are persisted next to the work they produced and surfaced in the
console *and* on the operator's handheld, as "why here?" at the rack.

Two details the original decision did not anticipate, both now load-bearing. The
published contract **refuses to express an unexplained directed movement**:
`PutawayInstruction.DecisionTraceId` is a `required Guid`, not a `Guid?`, while
the ledger column stays nullable because ad-hoc transfers legitimately have no
decision behind them — the stronger rule lives at the door directed work comes
through. And retention keeps a **spread of exclusions, not a prefix**: a putaway
considers two thousand candidates, and the first thirty rejections are thirty
dock doors failing the same constraint, so keeping the first N would store one
sentence thirty times and never mention the aisle that was the wrong temperature.

**Rule changes are testable before promotion.** Because the ledger is a complete
history ([decision 1](#1-inventory-truth-is-an-append-only-ledger)), a candidate
rule set can be replayed against the decisions that were really made and the
differences read off. Replay **re-runs both rule sets** over a reconstruction
rather than diffing two stored traces — a stored trace keeps only a bounded
sample, so it cannot say where an arbitrary candidate ranked — and the baseline
re-run doubles as a check on the reconstruction itself. If the rules that made a
decision no longer choose what they chose, the comparison is `Approximate` and
says so. Fidelity is measured, never asserted.

**What it costs**

- Every decision allocates and stores a trace, and traces need a retention
  policy.
- Rules must be expressible declaratively. A genuinely procedural rule has to be
  modelled as a scorer or it escapes the trace guarantee.
- Replay needs decision *inputs* reconstructible from history, which constrains
  what the ledger and task records must capture.
- **A vocabulary is typed against its own candidate and context**, so putaway
  rules cannot appear in an allocation rule set. The price is a second
  catalogue — a switch statement — and it is the right price.

**Did it generalise?** One mechanism and one rule set proves nothing. Allocation
was the second vocabulary and cycle counting the third, and both dropped in with
no change to the engine, the journal, the trace schema or the console component
that renders a trace. The structural surprise came from allocation: one decision
yields *several* lines, so the fill walks the engine's ranking taking what each
holding has, which keeps every judgement inside the trace.

The instrument that came out of all this is worth knowing about: `RuleInfluence`
reports what a rule was **worth** to a ranking — its weight times how far apart
it actually scored the candidates in front of it — as opposed to what its weight
claimed. Five scoring flaws in this codebase were a scale that could not express
a difference that existed, and it needed nothing new stored, because the scored
breakdown was already on every trace.

---

## 5. shadcn for behaviour, our own visual layer

**Radix primitives, every visual token replaced.** Radix brings focus management,
roving tabindex, dismissal, ARIA and correct keyboard semantics — weeks of work
most teams get subtly wrong, and the console is keyboard-first. But shadcn's
*defaults* are a large part of what reads as machine-generated: the stock neutral
ramp and radius are recognisable on sight. So the primitives were adopted and no
stock shadcn CSS variable survives. Where the native element genuinely does the
job — a filter `<select>` — it is used directly. Radix earns its place where the
platform cannot do the job, not everywhere.

**Colour means state, never chrome.** In a WMS colour carries operational
meaning: expired, blocked, quarantined, short-picked. The shell is near
monochrome so those signals are not competing with decoration, and `Badge`
deliberately has no "brand" tone.

**Two density classes from one token set.** `data-density="console"` and
`data-density="handheld"` on the root: the same semantic tokens at different
scale, 30px controls on the desktop and 52px on a scanner. The handheld's sizing
is a hard requirement — gloved thumbs, arm's length, fluorescent glare — so it
belongs in the system rather than in a bespoke stylesheet.

**Identifiers are not numbers.** `Code` and `Numeric` are separate components
because the distinction is easy to get wrong and expensive when you do.
Quantities, weights and dates are values and get `Intl` formatting for the
reader's locale. GTINs, SSCCs, SKUs and location codes are identifiers that
happen to contain digits — never grouped, never localised, always monospace.
Under a Turkish locale a number-formatted GTIN becomes `5.098.760.010.303`, which
is not a barcode and will not scan.

**The API returns codes; the client owns the wording.** Domain errors and scan
warnings travel as `{ code, args }`, never as prose. A server that returns
English sentences makes Turkish impossible without shipping translations into the
backend, and adding a language is now a catalogue rather than a backend change.
It has a pleasant consequence: because the server never formats for a human, it
needs no culture data at all, so `InvariantGlobalization` stays **on** — which
also removes the Turkish dotted/dotless-i casing hazard at the platform level
rather than relying on every future call site remembering `ToUpperInvariant`.

**Theme is three-state and per-device** — `system` / `light` / `dark`, persisted
per device rather than per account, because an office monitor and a handheld
under fluorescent light want opposite answers. A pre-paint inline script sets the
attribute before first render; resolving it in React is one frame too late, and a
white flash at shift start reads as unfinished.

**What it costs**

- The handheld bundle grew from ~64 KB to ~98 KB gzipped, almost entirely
  i18next. A real cost on a device where cold start matters, accepted because
  Turkish support is a requirement.
- Tailwind v4 does not scan across package boundaries. The `@source` directive
  pointing at `packages/ui` is load-bearing — without it shared components render
  completely unstyled, with no obvious cause.

The palette and the two deliberate deviations from it — Terracotta is not an
accessible text colour, and it sits where danger red normally lives — are in
[invariants.md](invariants.md#the-design-system-and-localisation).

---

## 6. The simulator is a client, not a module

Three preference-scoring flaws had been found in this system before this decision
was taken. None was found by the test suite. All three were found by a person
running `docker compose up` and reading a trace that looked wrong.

They share a shape: each was a preference normalised against the wrong scale, and
each was invisible until there was a **realistic candidate field** behind the
decision — a warehouse with hundreds of holdings in real racking, the same SKU in
several bays, stock picked down and topped up. A unit test builds two bins and a
pallet, and two bins and a pallet is precisely the field in which a broken
normaliser and a working one agree.

**So the simulator is a separate console project that drives the HTTP API,** and
`Countflow.Simulator` has **no project references at all**. It signs in, books
deliveries, does the jobs on the queue, ships the orders and counts the racking
through exactly the endpoints a handheld and a console use.

The alternative was a module, the shape every other capability here has taken. It
was rejected because of what it would have had to reference: Inbound, Outbound and
Counting publish *nothing*, so a simulator module would have needed project
references to three implementations and become the first thing in the codebase
permitted to reach past a module boundary — to build a testing tool.

Two things follow, and both are improvements rather than prices. **Everything the
simulator manages to do is something a real operator could have done**, so a
warehouse it builds is reachable in production; a module could have written the
tables directly and produced a warehouse that looks realistic and is not. And
**it has to solve its own problems the way a user would**: the counting policy
refuses to count a bay within four hours of anything moving in it, and a
warehouse built ninety seconds ago has nothing countable. The simulator cannot
age a timestamp — it has no database — so it clones the counting rule set, sets
the quiet period to nothing, scopes the copy to its own warehouse and promotes
it. That is the product's intended answer to the problem, exercised end to end,
including the rule-set editor.

**What it costs**

- It can only reach states the API allows, which is the point, and does mean
  setting up an awkward scenario takes more calls than writing rows would.
- It has no top-level statements, because those generate a `Program` in the
  global namespace and the integration tests resolve the host through
  `WebApplicationFactory<Program>`.

**It found a bug on its first run.** Built to reproduce a known flaw, pointed at
a warehouse for the first time, it found a fourth one that three rounds of
hand-driven walkthroughs had missed: a rule weighted 0.25 of the putaway set that
was realising 0.8% of the ranking, because its scale could not express a
difference that existed. The arithmetic, and the fix, are in
[history.md](history.md#phase-6-shadow-replay-and-the-warehouse-simulator).

---

## 7. The public surface is the surface

An outside integrator has to authenticate, read stock and decisions, and drive
both warehouse loops. The usual answer is a curated public API: a separate
module, a smaller and more stable set of endpoints, its own DTOs, its own
credentials, its own permission model.

Three reasons not to, in increasing weight.

**Architectural.** Such a module would have to reach Inbound, Outbound and
Counting to expose the loops, and all three publish nothing — so it would have
needed project references to three implementations and become the first thing
permitted to reach past a module boundary. The boundary would have been broken by
the very work whose subject was stability.

**A parallel surface is a second definition of the truth.** Every published DTO
is a claim about the domain, and two sets of claims drift. The system already has
an opinion about this one floor down: available stock is computed rather than
stored precisely because a second number is a second number to drift.

**And the one that actually settled it:** a curated surface is only more stable if
somebody keeps it stable, and the mechanism that would do that — "this endpoint
is internal, so I may change it freely" — is exactly the mechanism that lets an
internal endpoint rot. The console and the handheld are not a lesser class of
client. If the surface they use is allowed to move under them, it moves under
everybody eventually.

**So there is one surface, it is versioned, and everything is a client of it.**
All 58 paths sit under `/api/v1` with no unversioned alias and no private
sibling. The version lives in `ApiSurface.Prefix` and one `MapGroup`, and modules
map *relative* paths — so no endpoint author can put an endpoint outside the
version, and standing up a v2 is a change to the host rather than an edit to
fifty files. The two front ends consume it through a TypeScript client generated
from the OpenAPI document, and CI regenerates that document and fails on drift,
so an undocumented change breaks our own build before it breaks an integrator's.

Integrations authenticate with an API key belonging to a service account and are
authorised by **the same roles** a person holds. No scope vocabulary: scopes
beside roles are two sets of rules about one question, and the second set is the
one nobody updates.

**What it costs**

- **Every endpoint is public.** Anything mapped is something an integrator may
  build on, and the compatibility policy applies to all of it.
- **A role is the smallest unit of authority a key can hold**, so "read stock but
  never post an adjustment" is not expressible. The mitigation is one service
  account per integration holding the least role that covers its job, and the
  price is stated here rather than quietly paid off later.
- The stability an integrator gets comes from a written policy and a CI drift
  check rather than from a wall.

**The one thing that was added rather than exposed** is the ledger change feed —
a cursor over `StockMovement.Sequence`, which was already a total order over every
change inventory has undergone. Change data capture with no outbox and no second
copy of anything. It is a **read**, and it stays one: the pressure a public API
applies is toward a parallel write path for integrators, and Inventory has
exactly one posting path.

---

## 8. The handheld writes to a durable queue

[Decision 3](#3-two-react-spas-not-blazor-and-not-nextjs) chose a PWA on the
argument that the handheld "must keep working through WiFi dead zones". For a
long time it did not. The app shell was cached, so a device in a dead aisle
started; every write went straight to `fetch` and failed, so there was nothing to
do once it had.

The genuinely hard half was already solved. Every write command carries a
client-generated idempotency key, a retried command posts exactly once, and a key
reused for a *different* command is refused rather than replayed — enforced
against an append-only ledger by a unique index. Replaying a queue against that
surface is safe by construction.

**So every write goes through a durable IndexedDB queue, online or not.** There
is no offline branch in any screen. A queue reached only when `navigator.onLine`
is false is a queue that has never run on the happy path, and a confirmation that
goes straight to `fetch` is lost if the app dies between the tap and the
response — which is the dead-battery case the durability exists for. One path
costs a millisecond and makes "the command survives" structural.

**The command's idempotency key is its primary key** — not a field beside the
record, the identity of the record. A key regenerated on restore turns a replay
into a second command, and against an append-only ledger that is a pallet booked
away twice with nothing to show it happened once. Making the key the identity
means there is nowhere in the restore path where one could be minted.

**The queue replays the published endpoints and nothing else.** No batch
endpoint, no sync endpoint, no second write path — a replayed confirmation is
byte for byte the request the handheld would have sent at the rack, and the only
difference is what time it is. An array-shaped sync endpoint would have needed a
per-element result contract, which is this queue's problem restated on the wrong
side of the wire.

**Only assertions are queueable; decisions are not.** "These goods came off a
truck" and "I confirmed this job here" are things an operator may state with no
network. "Give me the next job" is a request for a server decision — one queue,
`FOR UPDATE SKIP LOCKED`, is exactly what stops thirty operators being handed the
same pallet — so a device answering it locally would be inventing work. The
corollary settles the design: **a claimed task can be worked offline if the
device already has it, and only then.**

**The queue is per-device in storage and per-operator in ownership**, and the
second half is correctness rather than courtesy. The ledger records the
credential that posted a movement, so replaying OP001's putaway while OP002 is
signed in would write a name into an append-only record against work that person
never did — a falsehood that is never corrected, only compensated.

**A refusal is news, and it goes in front of the operator in a form they can act
on.** This is the actual subject of the feature. A putaway confirmed in a dead
spot can come back with `work.task_already_completed` — somebody else did the job
— or `inventory.insufficient_stock`, and those are two different phone calls. The
reconciliation screen says three things per refusal and never fewer: **what you
did**, reconstructed from the device's own snapshot, because if the server
refused it there is nothing server-side to look it up from; **what the warehouse
says**, grouped by the kind of news rather than by error code; and **what to do
now**, because "count A01-01-02-A and tell your supervisor" is an instruction and
`inventory.insufficient_stock` is a shrug.

**Optimistic state is never dressed as success.** A held confirmation reads
"saved on this device", in warning tone. An operator told "done" for work the
warehouse has never heard of has been lied to, and finds out when somebody asks
them about a pallet a week later.

**What it costs**

- **Booking goods in offline answers nothing.** The receipt is an assertion and
  queues; the putaway suggestion is a decision over a warehouse the device cannot
  see, and it arrives when the receipt does. A device that guessed a location
  would be inventing a decision trace, which
  [decision 4](#4-every-decision-carries-its-reasoning) forbids outright.
- **A device holds work only one operator can send**, so a handheld can sit in a
  charging rack holding somebody's confirmations until they come back.
- **The device's view of a task can be stale**, and the operator learns it was
  when the confirmation is refused. There is no way to know sooner without a
  network.
- **Two stores now describe the same commands** — IndexedDB on the device and the
  ledger on the server — and they agree only because the idempotency key makes
  the reconciliation total. That is the same bet
  [decision 1](#1-inventory-truth-is-an-append-only-ledger) made about balances
  being a projection, one floor further out, and it is only safe because the key
  is enforced by a unique index rather than remembered by the client.
- 5.6 kB gzipped, measured — IndexedDB directly rather than Dexie, and the
  service-worker registration written against the browser API rather than
  `workbox-window`, because either wrapper would have cost about what the whole
  feature did.

**The service worker no longer updates itself**, and the reason is not
durability: a reload mid-drain is safe by construction, because the command is
already stored under the key it was born with. It is that a reload takes away a
half-typed count — the one piece of work on the device with no record anywhere —
and that `skipWaiting` lets an incoming worker drop the old precache under a page
that is still running. So the update is applied when the device is quiet.

---

## 9. The API publishes operations, not tables

Every widely used open-source system in this field generates its HTTP surface
from its data model. Frappe derives a REST resource from every DocType; Odoo's
`execute_kw` exposes the ORM's own methods — `search`, `read`, `create`, `write`,
`unlink` — over every model; InvenTree serves generated viewsets over its Django
models. The convenience is real, and it is why those systems are pleasant to
integrate against: one convention to learn, no endpoint to write, and anything
the schema holds is reachable the day it is added.

Countflow's 58 paths are written by hand, and each of them names something that
happens in a warehouse rather than a table that stores it.

**A generated surface and [decision 1](#1-inventory-truth-is-an-append-only-ledger) cannot both hold.** The ledger has exactly
one posting path, and that path publishes *operations*: `IStockPosting` offers
`ReceiveAsync`, `PickAsync` and `CountAsync`, never `PostAsync(movementType, …)`.
The shape is what carries the guarantees — a pick names the reservation it
discharges, so picking stock nobody promised is unexpressible rather than
rejected after the fact. Generated CRUD over `StockMovement` is a second door
onto that table with no operation behind it: the caller supplies a type and two
locations, and every question the operation was answering becomes a field that
may or may not be filled in.

**And a generated surface publishes storage, which is not the contract.**
`PutawayInstruction.DecisionTraceId` is a `required Guid` — the published
contract cannot express an unexplained directed movement. The ledger column
behind it is deliberately nullable, because an ad-hoc transfer legitimately has
no decision behind it. Two different shapes, and the stricter one is the one
outsiders are held to. A surface derived from the schema would publish the
nullable column and lose the distinction, which means the guarantee in
[decision 4](#4-every-decision-carries-its-reasoning) would survive in the C# and quietly not survive over HTTP.

This is the same argument [decision 7](#7-the-public-surface-is-the-surface) makes about a second, curated API,
pointed the other way: there, the risk was a parallel surface that drifts; here,
it is a generic surface that is accidentally more permissive than the one it
generalises.

**What it costs**

- **Every endpoint is somebody's afternoon.** There is no generic escape hatch,
  so a projection nobody anticipated is a change to this repository rather than
  a query an integrator writes.
- **A model with no operation behind it is invisible from outside**, which is
  intended, but it means "read every field of every table" is not something this
  API can do, and for some integrations that is the whole requirement.
- The surface grows with use cases rather than with the schema, so the gap
  between what the system knows and what it will tell you is real and permanent.
  `GET /inventory/changes` is the deliberate relief valve: a cursor over every
  quantity change ever made, for integrators who need to build a view we did not.

---

## 10. The warehouse owns quantity, the ERP owns value

Odoo and ERPNext both carry money on the stock ledger — a valuation method per
item, a cost on every movement, and postings into the general ledger as stock
moves. It is the obvious next column: the ledger already has the quantity and the
timestamp, and FIFO valuation wants exactly the history this table already is.

`StockMovement` has a quantity and no price, and that is deliberate.

**A movement's value is not a warehouse observation.** The floor knows a pallet
went from a dock to a rack. What it was worth depends on purchase price, landed
cost, the currency on the day, and an accounting policy — none of which anybody
in the building sees, and all of which the ERP already holds. A number this
system cannot check is a number it should not store, and the system already
refuses this class of thing one floor down: available-to-promise is computed from
on-hand minus open claims rather than stored, because a second number is a second
number to drift. A cost carried here and a cost carried in the ERP is that same
mistake across a network boundary, where the drift is harder to see and nobody
owns the reconciliation.

**The ledger's shape is what makes the split cheap.** `/inventory/changes` is a
cursor over every quantity change this warehouse has ever made, so an ERP can
value our movements against its own costs, on its own schedule, with its own
method, and never ask us what anything is worth.

**What it costs**

- **There is no valuation report, no COGS, and no perpetual-inventory posting.**
  "What is the stock in this building worth tonight" is not a question this
  system can answer, and for a warehouse manager used to reading that number off
  one screen, the answer is now in a different application.
- **Countflow cannot be the system of record for a business with no ERP.** That
  is a real segment, and this decision declines it.
- **Reversing this gets more expensive every day it stands.** The ledger is
  append-only, so a cost column added later is null for all of history — and
  history is precisely where FIFO and moving-average valuation are computed from.
  The reversal is not "add a column"; it is "add a column and accept that the
  books start today". Which is the reason this is written down as a decision
  rather than left to look like an omission.

## 11. A wave is a sequence of decisions, not a partition

Waving was deferred out of four phases on the same objection, and the objection
was worth taking seriously. The three vocabularies that exist rank a field of
candidates and hand back a ranking; a wave *partitions a set of orders*, and a
partition has no winner, no losers, and no "the constraint that excluded each
loser". The shape genuinely does not fit `Decision<T>` as stated, which is why
`DecisionKind.Waving` has sat in the enum since Phase 3 with no vocabulary behind
it and a `Wave` that is a grouping somebody made by hand.

The objection rests on a premise, and the premise is false.

**Two of the three vocabularies already produce sets rather than winners.**
Allocation walks the engine's ranking taking what each holding has until the
demand is met; cycle counting takes the top `N` and stops. Neither picks a
winner. What both do is put every scrap of judgement into the *ranking*, which is
traced, and leave the *consumption* of that ranking deterministic and dumb —
`Fill` and `Take` are each a short walk down the ranking, and each carries a
comment saying that anything cleverer would be judgement the trace could not
reach.
The set is not a second kind of answer. It is a ranking, consumed.

**And putaway already scores a candidate against accumulating state.**
`putaway.consolidate_same_item` asks what the bin already holds, and prefers a
bin holding the same lot over one holding the same item over an empty one. A rule
that judges a candidate against what has already been put somewhere is not a new
capability the engine needs; it is the oldest preference in the catalogue.

**So waving is admitted as a fourth vocabulary, and nothing in the kernel
changes.** The candidates are the orders not yet waved. The context is *the wave
being filled* — the same shape as `PutawayContext.Occupancy`. Constraints refuse
orders that cannot join this wave (carrier cut-off passed, wrong ship date, wave
at its cap) and say which. Preferences score fit against the wave so far: shared
carrier, shared zone, pick-density overlap, urgency against the cut-off. Then the
wave is filled by taking from the top until the cap, which is `Take` with a
different limit. Seed the next wave, run again. **A partition of `N` orders into
`K` waves is `K` evaluations of the engine that already exists**, each one a
ranking, each one traced by construction, each one replayable, each one read by
`RuleInfluence` exactly as the other three are.

`Decision<T>` keeps its internal constructor and its meaning. The journal, the
trace schema and the published `DecisionTraceView` are untouched. The console's
trace component gains a label for a third kind of candidate and no second code
path — the `aboutStock` boolean becomes a lookup by kind, which is the change
allocation already made once and is copy rather than structure. `ShadowReplay`
needs a fourth rebuild and a fourth `InputsJson` shape, which is the price
[decision 4](#4-every-decision-carries-its-reasoning) already names and accepts.

The door stays narrow the way the other three do: the advisor hands back a
`WaveAdvice` whose trace id is a `required Guid`, so rule-driven waving cannot
express an unexplained grouping. `SalesOrder.WaveId` stays nullable, because a
wave a supervisor built by hand legitimately has no decision behind it — the same
split, for the same reason, as the nullable trace column on the ledger beside the
required one on `PutawayInstruction`.

**Why not the other two.** Extending `Decision<T>` with a grouping was the
obvious move and it fails on the pillar waving exists to serve: a partition has
no winner, so `Decision.Winner` becomes a property that means nothing on one kind
in four, `SelectedCandidateKey` goes null on the trace, and `ShadowReplay.Compare`
— which is keyed on the winner, and treats two nulls as agreement — would report
every waving replay as `Unchanged`, silently and permanently. The one instrument
built to catch rule flaws would be blind on the newest vocabulary and would not
say so. A sibling `Partition<T>` is the honest version of the same idea and costs
more than it looks: a second trace shape, a second comparison vocabulary in
replay because `Changed`/`Reordered`/`Lost` says nothing about a grouping, no
`RuleInfluence` at all because swing and margin are ranking arithmetic, and a
console that branches — if not inside the trace component then across the five
pages that render one. Underneath all of that it builds the container for a
judgement that has no explanation to put in it: if a wave is chosen by optimising
over the whole open order book, "why is this order in this wave" has no per-order
answer, only an objective function. That is the thing
[decision 4](#4-every-decision-carries-its-reasoning) exists to refuse, and it is
[decision 10](#10-the-warehouse-owns-quantity-the-erp-owns-value)'s rule one
floor over: a number this system cannot check is a number it should not store,
and a judgement this system cannot explain is a judgement it should not make.

**What it costs**

- **The wave is greedy, and Countflow will not produce the optimal one.** The
  result depends on which order seeds each wave, and a better partition will
  usually exist. This is the trade `Fill` and `Take` each already made, and it is
  a larger gap here than it is for either of them — the distance between a greedy
  grouping and an optimal one is real money in picker-hours. It is declined on
  the merits, in the same register as the valuation split rather than as a
  limitation to be paid off later: an optimiser cannot be traced, and an untraced
  wave is one a supervisor starts overriding.
- **"Why is this order in this wave" is answerable; "why are there three waves
  and not two" is not.** The first is one trace. The second is a property of the
  sequence, and no single evaluation produced it. A supervisor will ask the
  second question, and the honest answer is a chain of traces rather than a
  document.
- **The chain is not a first-class object.** Every link is trace-guaranteed by
  construction, but nothing forces the chain to be complete the way
  `PutawayInstruction.DecisionTraceId` forces a single decision to be explained.
  That is a genuinely weaker guarantee than the other three vocabularies have,
  and it is weaker because the composition happens in the advisor rather than in
  the type.
- **Trace volume goes up an order of magnitude per run.** One putaway is one
  trace; one waving run is one trace per wave, each with its own retained sample
  of the losing field. The retention policy that
  [decision 4](#4-every-decision-carries-its-reasoning) says traces will
  eventually need stops being theoretical here.
- **The first input that is a previous decision's output.** `WavingInputs` has to
  record the wave's membership as of that evaluation or replay cannot rebuild the
  context. It is a list of order ids and it is recordable, but it is the first
  time a decision's inputs include something the system itself decided, and a
  replay of wave three is only meaningful if waves one and two replayed the same
  way.
- **The seeding trap is known in advance and will still have to be paid for.**
  The first order into a wave is scored against an empty context, which is a
  degenerate field — and the rule for those is already written down: a degenerate
  field must score **best, not worst**. This codebase has found that same
  normalisation flaw five times, most recently inside the fix for the fourth. It
  will be waiting at the seed of every wave. See
  [invariants.md § The rules engine](invariants.md#the-rules-engine).

**What it unlocks.** Batch picking, cluster picking and `DecisionKind.TaskAssignment`
are the same shape — a set carved out of a field of candidates against
accumulating state — and all three are now vocabulary work on an engine that does
not need to change to accept them.

---

## 12. A disposition is a verb, and returns belong to outbound

[Decision 11](#11-a-wave-is-a-sequence-of-decisions-not-a-partition) admitted a
fourth vocabulary on an argument rather than on evidence: that the kernel would
not have to change to accept one. Returns are the first vocabulary built since,
and they were chosen to be the least like the other three — so this section is
partly a design record and partly the receipt.

**The claim held.** `RuleEngine`, `Decision<T>`, `IRule`, `IRuleCandidate`,
`DecisionJournal`, the trace schema and `ShadowReplay.Compare` were not opened.
`DispositionRuleCatalogue` is `PutawayRuleCatalogue` with different rule names in
it; the console's trace component gained the lookup by kind that decision 11
predicted it would, and no second code path. The costs that were named in advance
— a second catalogue, a fourth `InputsJson` shape, a fourth replay rebuild —
are the costs that were paid.

### The candidate is not a row

Putaway ranks two thousand `StorageLocationSnapshot`s; allocation and counting
rank holdings. All three have a database identity that `IRuleCandidate.Id` can
simply *be*. A disposition has none: the field is four fixed verbs, so
`DispositionOption` mints four constant GUIDs by hand and keys them
`restock` / `quarantine` / `repair` / `scrap`. A deterministic hash of the enum
name would look tidier and would silently change the day somebody renamed a
member, and a trace cannot survive that.

Every evaluation considers all four. Narrowing the field to the plausible ones
before the engine sees it would be the `WHERE` clause
`FindStorageCandidatesAsync` refuses to be — a candidate excluded by a query never
appears in a trace, and "why wasn't it just repaired?" is the question this
decision exists to answer.

**And the field is small enough to be dangerous.** A four-element field walks
straight into the normalisation trap this codebase has now found five times, and
it walks into it from the *narrow* side, the way Phase 5's counting travel rule
did: a field-relative normaliser puts its best candidate at 1 and its worst at 0
however alike the two really are, and over four options — often two, once the
constraints have finished — that would be the fingerprint on every decision ever
made rather than on an unlucky few. The answer is that **every preference in this
vocabulary scores on an absolute scale.** `disposition.recover_value` returns
1.0 / 0.7 / 0.3 / 0.0 by outcome; `disposition.shelf_life_headroom` returns a
fraction of the item's own shelf life, which is the FEFO rule again and for the
same reason. The precedent is `putaway.consolidate_same_item`, which has scored
against fixed categories since Phase 3 and is the one travel-adjacent preference
that has never been one of the five. **Score against a scale that exists without
the field**, and a degenerate field has nothing to be degenerate relative to.

### A disposition is a verb, not a `StockCondition`

`Available` / `Quarantine` / `Damaged` / `OnHold` / `Expired` already existed, and
restock and quarantine map onto them cleanly enough that adding `Repair` and
`Scrap` beside them looks like the small change. It is the wrong one. A condition
is a state a quantity is **in**; a disposition is an action taken **on** it.
`Damaged` is a condition and `Scrap` is a verb, and the two coincide for exactly
two of the four by accident.

The ledger had already drawn that line and was waiting to be asked. Restock is a
`StatusChange` and then an ordinary `Putaway`; quarantine is a `StatusChange` in
place, which is what `MovementType.StatusChange`'s own documentation has said
since Phase 1; repair is a `Transfer` to the InTransit counterparty, which
`MovementShape` has permitted on either side of a transfer since Phase 1 because
stock leaving for another site was the first thing that was neither here nor sold;
scrap is the write-off Phase 5 built. **No new movement type, no new condition.**
What returns added to `IStockPosting` is two doors onto operations the ledger
could already perform — `ChangeConditionAsync` and `SendForRepairAsync` — and the
first of those is a door Phase 1 built the room for and nobody had walked through.

If a future change wants `StockCondition.AwaitingRepair`, that is the signal the
two vocabularies have fused, not that the enum was too short.

### Returns belong to outbound

A return is inbound goods against an **outbound** document, and ADR 2 says a
module never reads another's tables. That makes the ownership question real rather
than clerical, and the deciding argument is
`SalesOrderLine.ReturnedQuantity`: it is a projection over return lines, so
whichever module owns the return owns the sum. Put the aggregate in Inbound and
Outbound has to resolve every order line's returns across a schema boundary on
every read of an order — a cross-module lookup per line, on the busiest page in
the application, to answer a question about its own document.

So `Return`, `ReturnLine` and `ReturnLineDisposition` live in the `outbound`
schema, and the physical half goes through the published doors exactly as a
despatch does: `IStockPosting` books the goods, `IWorkQueue` raises the
inspection, `IDispositionAdvisor` decides their fate. Outbound still writes no
ledger entry, which is the claim it has been able to make since Phase 4.

### A return is two events, not one

The receipt already existed — `ReceiptInstruction.IsReturn` has been on the
contract since Phase 3 — so the cheap version of this feature was one call at the
dock that books the goods and dispositions them in the same breath. It is the
wrong shape, and the reason is visible in what the rules actually score on: sealed
or opened, repairable or not, what the goods *are* as against what the paperwork
says. Every one of those is a judgement somebody makes with the carton in their
hands. A disposition decided at the desk would be decided by somebody who has not
opened it.

So goods land in a hold-role returns bay at `OnHold` — a condition allocation will
not sell out of — an `Inspection` task goes into somebody's queue, and the
disposition waits. `WorkTaskKind.Inspection` is the first kind of directed work
that moves no stock and posts nothing; what it produces is
`InspectionFindings`, and that is the whole input to the fourth vocabulary.

### The rules advise; a person may overrule them, and the trace is written either way

The other three advisors are consumed by callers that act on the answer. This one
is not, because one of its four candidates destroys stock. The endpoint always
runs the evaluation and always writes the trace, **including when the inspector
has already said what they want** — an override that skipped the evaluation would
destroy the only record of what the system would have done, which is the one thing
that makes an override worth reviewing. `WasOverridden` is stored rather than
derived by comparing against the trace later, because a rule set is promotable
data and will have moved on.

Scrap additionally requires notes at the endpoint, mirroring
`ScrapInstruction.Notes` being `required`: every other movement is explained by
its document, and a write-off is explained only by the person who ordered it.
`disposition.scrap_is_not_for_saleable_stock` is the other half of that guard, and
it is a constraint rather than a heavy weight because every other mistake this
vocabulary can make is fixed by re-running the decision and this one is a pallet
in a skip.

### The correction it forced: a return credits Customer

Booking a return posted it **from the Supplier counterparty**, because
`MovementShape` required a `Receipt` to come from Supplier and nobody had needed
otherwise. The reason code said `CustomerReturn`, so the information was
recoverable; the *balance* was not. The despatch had debited Customer and nothing
ever credited it back, so the customer contra account only ever grew — and the one
account a distribution centre most wants to reconcile was the one account an
append-only double-entry ledger ([decision 1](#1-inventory-truth-is-an-append-only-ledger))
could not close.

`MovementType.Receipt` now permits Supplier **or** Customer. That is a widening of
a load-bearing guard and it is deliberately narrow: the type is the movement's
*shape* — goods entering the building from a counterparty — and which counterparty
is the business fact `MovementReason` already carries. Scrap and Adjustment stay
out, because stock does not come back from having been destroyed and an inbound
correction is an Adjustment by name.

**What it costs**

- **`RuleInfluence` says very little about this vocabulary, and that is accepted
  rather than worked around.** The instrument that caught four of the five scoring
  flaws measures a rule's realised swing across the admissible field. Over four
  candidates — often two — `UsesTheFullRange` fires on every categorical
  preference by construction, `DistinctValues` is 2 or 3 on every line, and
  `WinningMargin` is arithmetic over a handful of points. The report is not wrong;
  it is uninformative, and a noisy instrument is one people stop reading. The
  vocabulary was still built on the engine rather than as a decision table,
  because the alternative forfeits the trace, the version stamp and replay — which
  are the three things the exercise is for. But nobody should expect the
  diagnostic to transfer.
- **The seeded weights are the least evidence-backed in the system.** Putaway,
  allocation and counting were tuned against a warehouse being driven by hand.
  `recover_value` at 0.40 against `handling_cost` at 0.25 is a defensible starting
  point and nothing more, and the two pull in opposite directions on purpose —
  which means a warehouse that gets them wrong will not restock slightly too
  often, it will scrap things. The rules are data and the preview endpoint exists;
  that is the mitigation, and it is a weaker one than "we watched it run".
- **A restock is two decisions and two traces.** "Should this go back on a shelf"
  and "which shelf" are different questions with different rule sets, and the
  honest answer is two evaluations. Nothing links them: the disposition trace
  does not name the putaway trace, so "why is this pallet in that bay" and "why is
  it back in stock at all" are answered separately and a person has to hold both.
  It is the same weakness decision 11 names for the wave chain, arrived at from a
  different direction.
- **`disposition.hazardous_never_restocked` ships disabled and is therefore
  untested end to end.** It is a real, supported rule that most warehouses should
  not run, and seeding it switched off is the honest default — a supervisor cannot
  enable a rule they have never been shown. But the seeded set is shared across
  the integration database, so no test enables it, and its first real exercise
  will be in a warehouse that handles regulated goods.
- **Repair has no return leg.** `MovementReason.RepairReturn` exists and nothing
  raises it. Goods go out to InTransit and come back through the ordinary receipt
  path, which conserves stock correctly and does not connect the two ends: nothing
  says which repair a receipt settles, and nothing chases a vendor who has had a
  pallet for four months. That is a real gap rather than a scope decision, and it
  is the first thing to build on top of this.
- **The condition change and the putaway are not one transaction.** They are in
  three schemas (ADR 2), so a crash between them leaves stock released to
  `Available` in the returns bay with no putaway task — visible as a pallet in
  returns, repairable by a retry, and the same convergent shape the outbound
  release already has.

---

## Adding a decision here

Add a section when a choice would be expensive to reverse and somebody will
otherwise undo it without knowing what it was for. Number it, add it to the list
at the top, and keep the shape: **the problem, the decision, and what it costs.**

A section with no cost stated is an advertisement rather than a decision record.
And when one of these turns out to be wrong, add the correction next to the claim
rather than editing the claim away — how a plausible belief survived is usually
the useful part.

---

## 13. A label kind is code, a label template is data

Nothing in the warehouse carried a printable identity. `LicensePlateId` ran
through every contract as a column with no label behind it, so a pallet had an
identifier the system knew and the floor could not read.

The obvious move is the one [decision 4](#4-every-decision-carries-its-reasoning)
already made for rules: hold label templates as data, so a new label is a record
rather than a deploy. **That claim is half right, and the half that is wrong is the
half that decides the architecture.**

A rule set is data because `RuleDefinition.Code` names an implementation that is
code. The configuration moves; the vocabulary does not. Applied to labels, the
same split says a **label kind** — pallet, location, carton — is code, because a
kind is a *binding contract*: which facts the system will go and fetch, from which
published surface, and what each is called. A **label template** is data, and what
it buys is layout and site variation: a customer who wants their part number in
40-point type, a site whose printer takes 4×6 rather than 4×8. Those are the edits
that otherwise happen at six in the morning with a pallet waiting.

A kind invented in a console would be a template that can reference anything, and a
template that can reference anything needs an expression language to reference it
with — which is the trade `RuleDefinition` already refuses, and refuses harder
here, because **the check is the whole safety story**. A template is validated by
*rendering it* against a specimen, the way a rule set is validated by *building
it*, and there is nothing to render against if the fields are not known in advance.

**So the template is a structured element list, not a ZPL string with holes in it.**
A pasted blob out of a label designer is cheaper by a wide margin and untestable
past "a string came out". Three things decided it the other way, and only the third
is about tooling. A GS1 barcode needs FNC1 in some positions and not others
depending on which AIs are present, so a `${sscc}` hole cannot be filled correctly
without re-parsing the designer's output. Values have to be escaped — a caret in an
item description is a ZPL command, and in a blob it silently truncates the label,
which does not throw and does not look wrong until a customer rejects a pallet. And
a template that renders can be rendered against a specimen carrying every optional
field absent, which is the pallet a layout is most likely to break on and least
likely to be designed against: the one labelled before it was built.

**The binder asks; the caller does not assemble.** A pallet label wants a SKU, a
description, a batch, an expiry, a quantity and an SSCC, which belong to MasterData,
Inventory and the receipt across three schemas. Handing that to callers means every
future caller reassembling it with its own idea of which GTIN goes on a pallet — and
AI 02 is the GTIN of the *contained trade items*, the case's and never the loose
unit's, which is the single most common way a logistics label is wrong. So the
kind's binder gathers it through published contracts (ADR 0002), and the caller
supplies only what a caller can know: which subject, at which site.

**Its own module.** It has the outward shape of Rules — versioned, promotable, with
a draft/active/archived life — but a label makes no judgement: no candidates,
nothing excluded, no trace, so filing it there would borrow the machinery for the
resemblance rather than the reason. MasterData is the closer fit for a template and
the wrong home for a `LabelPrint`, which is an operational event rather than
reference data, and MasterData has no promotion lifecycle to lend. Under
[decision 2](#2-a-modular-monolith-one-schema-per-module) a module costs one
schema, one `DbContext` and one line in `ModuleCatalog`, and it buys not having to
make either of those two mistakes.

**Printing is recorded, and it is not a movement.** ADR 0001's own test settles the
second half: printing moves no stock, so a ledger entry would put a fact that is not
about a quantity into an append-only record of quantities — the same reasoning that
keeps a reservation and a no-variance count out. But a reprint means two physical
objects in the building each claiming to be the same pallet, with goods received
twice against one licence plate at the end of it, and "who printed the second one"
is the first question anybody asks. So `LabelPrint` is its own table, stamped with
the template's id, name and version exactly as a decision trace stamps its rule
set's — because the template will be superseded and a print from last year has to go
on saying what produced the piece of paper somebody is holding.

**What it costs**

- **A second language nobody asked for.** A warehouse that has a design its label
  software produced cannot paste it in. That is the real price of the structured
  template, and it is not paid back until the first template that would have been
  silently broken.
- **A new label kind is a change to this repository**, which is exactly the claim
  the original issue wanted to avoid making. It is made deliberately, and the
  narrower promise — layouts without a deploy — is the one that survives contact
  with `RuleDefinition`'s argument.
- Every optional field is a way a layout can come out blank, so a template author
  has to design for absence. `LabelFieldDefinition.IsAlwaysPresent` is published so
  they can, and the specimen is built to make ignoring it fail at save time.
- **Transport is deliberately absent.** A direct socket to port 9100, a local print
  agent, WebUSB and cloud print have genuinely different trade-offs and none of
  them is needed to verify a generator — the ZPL is the output, and asserting on it
  needs no hardware. See [ROADMAP.md](../ROADMAP.md).

---

## 14. Countflow issues its own licence plates

Printing a pallet label at a dock does not work without answering where the SSCC
comes from, and the answer was not in the system. `DockReceiptRequest.Sscc` is
optional and `StockReferenceResolver` creates a licence plate only when one is
supplied, so a **blind receipt** — no advice note, no supplier label, the commonest
receipt in a real warehouse — produced a pallet with no `LicensePlateId` at all.
Not a plate without a label: no plate.

Three ways out. Refuse to print without a supplier SSCC, which makes the first
target useless on exactly the receipts that need it most. Print an internal
identifier as a plain barcode, which gives one pallet two identities and quietly
contradicts what `Sscc` is documented to be. Or **mint**.

**So a warehouse issues SSCCs on the company's own GS1 prefix**, held as
`Warehouse.Gs1CompanyPrefix` — reference data somebody is given by GS1 and types in
once. A site with no prefix is refused rather than given a plausible default: a
made-up prefix produces eighteen structurally valid digits belonging to somebody
else's company, which is worse than no label, because it scans, resolves, and is
wrong everywhere outside this building.

**This is the only thing Countflow emits that it cannot correct.** Everything else
can be fixed by a compensating entry; an SSCC goes out of the door stuck to a pallet
and is read by a haulier, a customer and possibly a customs system. So the rule is
stricter than anywhere else in the codebase: **a serial reference is allocated once
and never reissued** — not after a rollback, not after a failed print. That is why
it comes from a PostgreSQL sequence rather than a `MAX(serial) + 1`: `nextval` does
not roll back with the transaction that drew from it, which is normally a nuisance
and here is the entire point. Burning a serial nobody used costs nothing. Two
pallets carrying one SSCC costs a customer receiving goods against a licence plate
that already means something else.

And it fixes when a plate becomes real. Until now that was a side effect of posting
stock; printing inverts it, because the label is made *before* the pallet is built
and from the moment it exists there is an object in the building carrying that
number. `ILicensePlateRegistry.EnsureAsync` is the published seam, and it is
`Ensure` rather than `Create` because a reprint must land on the plate the first
print made.

**What it costs**

- **A configuration step with no sensible default.** A fresh install cannot print a
  pallet label until somebody enters a GS1 prefix, and the refusal has to be legible
  enough that they know what to go and buy.
- The serial space is finite and its size depends on the prefix — a ten-digit prefix
  leaves six digits and a million pallets. Exhaustion is a refusal, and the remedy
  is a purchase from GS1 rather than a code change.
- **Licence plates now exist with no stock on them**, which is a state nothing
  before this could produce. It is the correct state for a printed-and-unfilled
  pallet, and every reader of a plate has to tolerate it.


## 15. Sequence the picks that were claimed together

Allocation chooses stock; routing orders the resulting work. These are separate
operations. A per-bin ordinal cannot express S-Shape when an aisle is skipped,
so routing operates over a set, using the full zone grid and a registered
strategy (`route.sequence_ordinal`, `route.s_shape`, `route.return`, or
`route.midpoint`). Zone settings are data; adding a strategy is code.

The Work module locks a bounded set of ready picks in one warehouse and priority
band, then sequences exactly the rows it claimed. A pick tour records the owner,
original stop count, strategy applied per zone, fallback reason, and travel
estimates. Travel steps are an ordinal proxy, not metres or elapsed time, and
savings may be negative. Missing geometry yields unknown measurements rather
than invented numbers. Unsupported geometry falls back to sequence order at
claim time; configuration saves refuse unsupported settings.

Claims use an operator-scoped retry key and a transaction-scoped advisory lock
for concurrent retries of that key. Releases keep completed stops and return
only unfinished tasks. Completion and release lock task rows to prevent a
release overwriting a completed stop. Transactions run through EF's execution
strategy. No cross-module table joins are introduced.

A handheld may work already claimed, cached stops out of signal. Completion
still uses the durable outbox. Claiming and releasing need a server response;
a tour with unsent or unresolved physical confirmations cannot be released
from the handheld. The device shows queued confirmations separately from
accepted completion. The existing single-task flow remains available.


## 16. Persist a run before invoking its handler

Recurring schedules are deployment configuration and handlers are code, rather
than a general-purpose executable job editor. A Jobs module owns the scheduling
state, while registered handlers call published module contracts in a fresh
service scope. Inventory reconciliation is the first handler, replacing its
private timer without changing its settings or read-only behavior.

A dedicated, nonpooled PostgreSQL session holds a job-specific advisory lock.
The runner persists a pending run ID before invoking the handler and retains it
across failure, cancellation, and process restart. Failed attempts use bounded
backoff; successful runs advance from completion instead of replaying every
missed interval. A failure in one job does not stop independent jobs.

The boundary is at-least-once. A handler may finish while the runner cannot save
its success, or lose its lock connection while still executing. Therefore a
write handler must pass the run's stable key to its owning module's idempotent
operation. A scheduler cannot manufacture exactly-once warehouse effects.

Dispatch tracks each in-flight job independently, so a blocked handler does not
delay other schedules. Every attempt writes a fresh concurrency token before execution;
success and failure saves reject stale attempts after lock loss, preserving a
replacement run’s identity and due time.


## 17. A scheduled count is an ordinary policy plan

Automatic counting is opt-in per warehouse through the supervisor's Counting
screen. The policy should be observed before activation; installing the runner
does not enable any warehouse. Persisted schedules supply a bounded task budget
and elapsed cadence, rather than a second source of rules or a cron calendar.
Counting publishes configured warehouse instances of its code handler through a
scoped catalog. Jobs tracks them independently, so a blocked warehouse does not
stall other schedules. Global runner settings remain separate from the editor.

Each warehouse's pending run ID is saved before CountPlanning starts. Plan lines
derive their enqueue keys from that plan key, so restart and lost acknowledgements
converge on the same tasks. Partial enqueues leave the warehouse pending until
retry repairs them. A reserved system actor identifies the origin; a human still
performs the count. Disabling stops future plans without abandoning pending work.

Manual and automatic planning, schedule editing, and cancellation share a
warehouse lock. Scheduler state uses a concurrency token to reject stale saves
after lock loss. A warehouse fence rotates in the transaction of each planning
write, guarding even manual intent creation after a competing lease. Cancellation resolves every intended enqueue before withdrawing
its task, so even a stale planner later receives an already-cancelled keyed task.
Retry does not resurrect a closed plan. No transaction spans module writes.

## 18. Min/max replenishment is reserved warehouse work

A fixed pick face has one item and an opt-in supervisor policy. Classic min/max
uses physical on-hand plus outstanding incoming replenishment: below minimum,
raise work toward maximum. Outbound allocations do not reduce this threshold;
order-demand replenishment is a separate strategy. This follows the distinction
in [Oracle WMS](https://docs.oracle.com/en/cloud/saas/warehouse-management/26a/owmol/overview-of-replenishment.html)
and [Dynamics 365](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/replenishment).
Incoming work counts before reading stock, so an overlapping completion can
conservatively delay a top-up but cannot disappear from both observations.

Replenishment owns its policies and durable plans. The Jobs catalog probes each
configured face independently. A face session lock and policy concurrency fence
serialize configuration, planning, and cancellation. Intent is saved before
cross-module effects, with stable keys per line for Inventory reservations and
Work tasks. Bulk-only sourcing is a mandatory allocation constraint retained in
the normal decision trace and shadow replay, rather than a configurable rule
that could accidentally source another pick face.

A plan uses at most 50 source holdings and can raise a partial top-up. The
unfilled quantity stays visible; exhausted bulk creates no empty plan. The next
probe reevaluates the remaining need. Quantities remain base units, with no
rounding above the face maximum or known volume/weight limits. Full-pallet and
case-rounding strategies are not inferred from a min/max policy.

The handheld confirms the fixed destination through the existing durable outbox.
Inventory posts a Replenishment movement and consumes its source reservation;
Work then records completion. A lost acknowledgement replays the same movement,
including the consumed reservation's identity. Cancellation resolves intended
keyed tasks first, atomically refuses if any task is assigned, and releases stock
only after withdrawal. Disabling a policy stops new plans but lets pending intent
recover and existing work finish. No transaction spans module writes.

## 19. Webhooks deliver a saved slice of the ledger

Inventory publishes its existing change feed through a contract; the HTTP pull
surface and stability watermark remain unchanged. Webhooks owns subscriptions,
immutable deliveries, and inbound trigger requests. Each subscriber advances its
cursor in the same transaction that saves a batch body, before network I/O. Its
pending batch blocks later batches, including after dead-lettering. An operator
can pause or retry it without skipping stock changes.

Delivery is at-least-once. A successful receiver may lose its acknowledgement, or
a worker may lose its lease while sending. Every retry has the same delivery ID
and exact UTF-8 body, with a base64 HMAC-SHA256 digest. Receivers deduplicate by
ID and validate the raw bytes with a constant-time digest comparison. Signed
challenge/echo verification proves control of the receiver before activation.

Network destinations are public HTTPS by default. Exact origin allowlists are
explicit deployment exceptions, not user-controlled settings. DNS answers are
validated and the selected address is pinned to the socket; redirects and proxies
are disabled. Responses and overall requests are bounded. Signing secrets are
issued once and stored encrypted with a separately configured AES-GCM key.
Production must persist that key across restarts and replicas.

Inbound capability URLs store only a token hash, are issued once, can be revoked,
and are excluded from application request logs and incoming traces. They queue
read-only inventory reconciliation, chosen explicitly for this first version.
A required caller retry key resolves to one durable request. The Jobs catalog
executes requests through the registered Inventory handler, without another
posting path or knowledge of Inventory tables. Reconciliation observations may
repeat if their acknowledgement is lost; stock is never changed by this trigger.

## 20. Operational dashboards keep their denominators

The existing Accuracy screen remains the diagnostic view. Operations adds a fixed
warehouse dashboard with a bounded 7/30/90-day selector, completed work, current
backlog, count accuracy, and storage occupancy. It composes owning-module reads
rather than introducing a cross-schema reporting service or query builder.

Throughput uses completion time, not task creation time. Backlog includes ready
and assigned work of every age. Daily buckets are UTC calendar days, with today
explicitly partial; zeros are measured zeros and all dates remain present. Work
aggregates in PostgreSQL with a covering index for warehouse/completion time.
The count-accuracy query receives the same window and retains its counted
sample denominator. An unmeasured accuracy rate is not perfect accuracy.

Density is a current observation, independent of the historical window. Slot
occupancy is stocked active bulk/pick-face locations over eligible locations,
excluding inactive area/zone hierarchies. Group by zone identity, not code:
several areas can contain AMB. The location contract supplies area metadata;
Inventory never joins MasterData's tables. Cube utilization is reported only
when every relevant capacity and occupied item volume is known, without masking
values above 100 percent. Missing measurements remain null.

The UI refreshes every 30 seconds, keeps errors visible, and stamps the oldest
Work/storage observation it shows. These independently observed figures are not
a transactional warehouse snapshot. Optional definitions open through circled-i
help; quantities, denominators, empty states, and failed-refresh status stay visible.

## 21. A camera inspects a barcode; it does not confirm warehouse work

The handheld has a read-only barcode screen using native `BarcodeDetector` and
its existing GS1 resolver. The purpose is demonstrability on an ordinary phone;
a keyboard-wedge scanner remains the floor input. Native capability varies, so
DataMatrix support is checked before camera permission and unsupported devices
retain manual entry. There is no fallback decoder dependency or uploaded video.

A decoded GTIN is not a receipt or a task confirmation. Catalogue packaging,
tracking values and warnings remain visible for inspection, without guessing a
quantity or posting stock. Camera and keyboard strings use the same server
parser, including group separators. Multiple distinct codes require reframing,
and cancellation owns the permission/setup/detection races as well as the live
stream. Offline capture can wait for an explicit resolution retry; it cannot
claim that an unknown barcode was recognised.

## 22. Cardinal grammar belongs to the locale, not the screen

Countflow already uses i18next, whose cardinal selection follows
[`Intl.PluralRules`](https://www.i18next.com/translation-function/plurals).
Plural families use `_one`/`_other` in both catalogues, even where Turkish's
numbered nouns have the same wording. `translateCount` constrains callers to
those families and requires a finite number. Display formatting cannot masquerade
as a numeric count; neutral total labels use a separate interpolation argument.

Rules send day/hour values as strings. Their translation adapter supplies a
numeric count for selection while retaining every named argument. Malformed
values fall back to the other form rather than crashing a decision trace. Several
independent counts in one summary use concise labelled quantities, avoiding a
second plural engine or deeply nested interpolation. Tests inspect actual
catalogue objects so inline keys, quoted keys, and complete placeholder paths
are checked as well as standalone lines.

## 23. FIFO ages a physical holding, not an accounting layer

FIFO fills the undated-stock rotation gap with `allocation.fifo`, an opt-in
preference using original receipt age. Existing policies remain unchanged.
Original age survives internal transfers, consistent with
[warehouse receipt-date guidance](https://docs.oracle.com/cd/E18727-01/doc.121/e13434/T210618T210921.htm);
choosing a location by its oldest age also follows
[location-directive FIFO](https://learn.microsoft.com/en-us/dynamics365/supply-chain/warehousing/location-directive-inventory-picking-aging).
This does not add LIFO or inventory valuation.

Countflow allocates one holding at a time. When several receipts share that
coordinate, the holding retains its earliest age until empty; it does not claim
an individually identified FIFO layer remains after a partial draw. Unknown-origin
stock, such as a discovered adjustment, remains unknown. Known dates normalize
across the allocation field like its travel preference; configured weights may
trade rotation against travel or a pick face. Missing ages stay neutral and the
trace says so, rather than inventing a receipt date.

Inventory owns the rebuildable age field on balances, updated under the same
locks and transaction as quantities. One pure age transition serves live posting
and the historical fold. A migration adds the nullable field and a one-time
completion marker; the migrator reuses the historical fold and bulk-writes derived
ages atomically. It leaves ledger entries and quantities untouched, and declines
to claim an age for a drifted quantity. Live and shadow allocation use the same
age window, so replay measures the historical policy instead of today's stock.

## 24. A blind count observes a whole bin

A holding-shaped task cannot discover an unexpected item in an allegedly empty
bin. BinCount is an additive location-only Work kind. A supervisor chooses the
physical bin and supplies a reason; this is explicitly a manual audit, rather
than a rule evaluation with a fictitious decision trace. Existing policy-led
holding counts remain unchanged.

The operator observes all holdings, distinguishing item, lot, pallet and condition,
or explicitly confirms empty. Inventory reconciles the union of observations and
positive book holdings; absence is an observed zero. Work never reads Inventory
tables. The narrow blind-count contract returns an opaque movement version and
an immutable confirmation receipt.

Stock cannot move unnoticed between the start and confirmation: a changed bin
version refuses the audit and requests a recount. A claim ID also fences old
offline observations after release/reclaim or restart. Inventory briefly takes a
table lock on balances when capturing the baseline and when comparing versions
and posting the whole correction. Baseline capture first drains in-flight writers:
an earlier sequence may commit after a later one on a different holding. This
serializes stock writers during baseline capture and confirmation. The input
accepts up to 200 observed holdings. It excludes phantom holdings and preserves atomicity using the
existing posting engine. The ledger can participate in its module's enclosing
transaction, while ordinary postings retain their existing transaction ownership.

Agreements, variances and the bin receipt commit together. The receipt retains
the exact original findings, including an empty-bin observation, and refuses a
changed payload under the same key. Work closes only afterward. If that final
write fails, the saved confirmation retries against Inventory's receipt. Release
and restart refuse to invalidate a confirmation already recorded. Draft captures
and queued confirmations are isolated by operator and claim on the device.

## 25. A repair replaces a promise, and its writes keep the lock session

A stranded commitment is repaired for one order line by an explicit supervisor
request. The old allocation keeps its quantity, source and decision trace; a
repair record links its withdrawal to the replacement plan and receipt. The
replacement plan is saved before reserving stock. This makes interrupted writes
recoverable with the same reservation and pick intent keys, including Work tasks
whose IDs Outbound never managed to record. A partial replacement remains a
shortfall, rather than cancelling the customer's order to hide it.

An advisory lock on an idle connection does not protect writes on another
connection after its session dies. An Outbound-only concurrency fence notices
too late: Inventory may already have reserved or despatched, or Work enqueued.
Order mutations therefore pin their scoped module contexts to the physical
session holding the order lock. Transactions remain sequential and owned by each
module; no transaction spans schemas and no module reads another's tables.
Pinned commands fail closed on session loss and never transparently reconnect.
The whole request resumes with its durable intent under a new lease. Ordinary
module requests retain the existing provider retry policy.

The cost is deliberate: order orchestration cannot issue parallel database
commands, and a transient session failure reaches the caller instead of being
retried inside a module. Monotonic downstream fencing in every participating
write contract would also work, but would add order-orchestration vocabulary to
Inventory and Work. Keeping the lock and the side effects on one session avoids
that coupling while retaining their transaction boundaries.

## 26. A mixed load shares rules, not a representative SKU

The first item unloaded onto a pallet cannot represent the capacity,
temperature or lot restrictions of everything unloaded after it. Repeating a
single-item evaluation independently would still miss the combined cube and
weight, the incoming SKU union, and two incoming batches of the same item.

Whole-load evaluation therefore has its own typed context, adapting the
existing putaway catalogue rather than adding a second rules engine or a second
set of configuration rows. Item-specific constraints inspect every line;
load-specific constraints and preferences run once over the combined load.
Unknown dimensions stay unknown, on the same terms as single-item putaway.

Consolidation and velocity average each SKU's score equally. Within one SKU,
lots are weighted by quantity. A thousand screws and one appliance are not a
thousand votes against one, and splitting a delivery into more lot lines does
not earn it extra influence. The trace note records the per-SKU percentages so
the average remains explainable. This is a chosen policy, not a claim that base
units across unrelated products are comparable.

The cost is an explicit adapter for each existing rule kind. A future rule must
decide whether it concerns an item or the entire load; an unknown adapter fails
closed. The original single-item context and catalogue retain their behavior.
The caller supplies the exact contents it is asking about; the advisor copies
them before awaiting and stores them on the trace. That list records decision
intent, not a second warehouse snapshot. Bin occupancy still comes from the
ledger during replay. The optional contents field is also the explicit replay
discriminator: old traces containing a plate ID remain single-item questions,
and growing the plate later never broadens their meaning. Live and historical
whole-load evaluations share one context builder. Inventory reads physical
holdings once for every requested item/lot perspective, including quarantined
stock, rather than combining separately timed occupancy queries.

Task revision history and re-slot orchestration are described in decision 28.
These advisory services do not themselves coordinate task ownership or guarantee
that a caller's supplied contents are still physically current.

## 27. A receipt reserves its handoff before it posts stock

Stock-first receiving had a gap: Inventory could commit a receipt, then Work
could complete the existing pallet task before receiving its source. That move
took the new stock too; the delayed enqueue then raised a new job for an empty
dock. Re-slotting or a task version alone could not repair that missing handoff.

Inbound now registers a durable receipt intent in Work before booking SSCC
stock. Work binds the retry key to the request fingerprint. An SSCC lane
serializes registration and acknowledgement even before Inventory creates the
plate, and later maps that lane to its opaque plate ID. A pending count on the
task blocks claims, confirmation and withdrawal. New receipts onto assigned
pallets are refused; the operator hands the task back before growing the load.

Work acknowledges the intent in the same transaction as its source append and
quantity update. A definite Inventory refusal clears pending state; an ambiguous
failure leaves evidence to resume with the original key. No transaction spans
modules, and Work's intent is not a second inventory booking.

Receipts whose source keys predate the protocol retain their existing Inventory
replay checks without acquiring a new intent. There is no historical request
fingerprint to infer honestly. Creating a pending intent for such a replay would
strand it behind Work's existing-source fast path, or wrongly refuse a receipt
whose task is already assigned. New receipts bind their fingerprint normally.

The task counter is a deliberate projection. Claim must recheck an updated task
row after waiting for its lock; a child-table NOT EXISTS check can retain an old
statement snapshot and miss a just-committed receipt. All lane/task writers lock
in that order. The cost is a retained intent per pallet receipt and a requirement
to retry interrupted receipts before taking the load. Whole-pallet decision
revisions and durable completion intent are described below.


## 28. Re-slotting records the decision the operator must acknowledge

A pallet can grow after its first receipt, while the first item's suggestion
remains attached to its Work task. Re-slotting is an explicit supervisor action
on Ready work: evaluate every current item and lot, record the reason and actor,
and append a revision pointing to both the old and new explanations. The original
trace remains unchanged. Task quantity reflects the evaluated holdings while
receipt sources retain their original quantities. No winning candidate is also
an explainable decision.

Work serializes the action against receipt acknowledgement, claiming and other
re-slots using its task row lock. A task-scoped idempotency key binds the reason;
retries return the original applied revision alongside current task state, even
if the task has since moved on. Console stores that key and reason before sending
and retains them across an ambiguous response and reload.

A later receipt marks revised advice stale, blocking claim and confirmation until
another evaluation. Handheld confirmations carry the exact trace ID shown when
queued; Work refuses an old trace before stock posting. List refreshes discard
cached explanations when their trace ID changes rather than retaining an old
explanation beneath a new suggestion.

Inventory checks the saved revision contents inside the whole-pallet posting
transaction. Every ledger writer takes compatible balance-table intent, then
sorted plate locks, then balance locks. The same plate lock spans replay, contents
read, comparison and posting; a new item/lot/condition row cannot bypass it.
Table intent goes first to respect blind counting's stronger table lock. Normal
writers remain concurrent across different plates. Replays validate their
original movement family and supplied contents before returning the old result;
they do not compare against an origin the first attempt has already emptied.

The cost is retained revision history, an explicit reslot after growing a revised
pallet, and serialization of physical writers on the same plate. Durable Work
completion recovery after loss of its lock session uses the intent below.

Inbound returns advice from the effective task after enqueue, including on source
retry. It reads one Work summary rather than reporting the incoming line's new
trace while the task retains an older explanation. Counts follow the task's
immutable trace. Stale advice returns its trace/revision with no destination; the
handheld asks for a supervisor re-slot. A receipt retry can therefore observe a
newer task revision, while its booked stock movement remains idempotent.


## 29. A refused pallet command cannot later become stock

Work completion recovery needs more than a pending flag. If one attempt refuses
and releases that flag while an older request is delayed, the older request could
later succeed after physical contents change. Work would then be free to re-slot
or receive onto a pallet that the delayed request is about to move.

Inventory records a terminal whole-pallet command result in the same transaction
as its stock batch. A global root-key row serializes requests even when competing
bodies name different plates. Its fingerprint binds plate, origin, destination,
task, trace, actor identity, notes and optional expected holdings. Holdings are
ordered and decimal scale normalized; a renamed operator remains the same user.
The original movement and outcome retain their original audit identity.

A committed success replays its movement identities; a committed refusal keeps
returning that refusal even if contents later match. Correcting the operation
requires a new key. Exceptions leave no partially committed journal or movements;
commit ambiguity is resolved by looking up the same root key. Pure structural
validation happens before the journal and remains deterministic for that body.

Legacy movement families are recognized before reading today's origin. A matching
legacy success gains a journal outcome, while a conflicting attempt rolls its
new journal row back so it cannot poison the original key. Existing plate and
balance locking still protect physical contents; this journal owns command
finality, not a second stock ledger.

The cost is retained terminal results for whole-pallet commands. This is the
Inventory half of durable completion recovery; Work owns the orchestration below.


## 30. Pallet confirmation survives the Work lock session

Work commits a completion intent and task pointer before calling Inventory. The
intent freezes the original instruction, actor identity and display name,
destination, override reason and decision trace. Its task-scoped key binds the
request body and actor identity. Inventory runs without an open Work transaction;
a separate task-locked transaction settles the durable terminal outcome.

A pending pointer blocks claim, release, cancellation, new receipt sources and
new re-slots. Already acknowledged receipt and re-slot retries still return their
original evidence. Task and receipt responses expose `completionPending`; receipt
responses suppress the destination while it is pending. The operator resumes the
original device command with its original key and body. Exceptions retain the
pointer, including lost commit acknowledgements. A username change cannot rewrite
the original audit identity.

A definite refusal is saved and releases only its own pointer; changed revised
contents also mark advice stale. A corrected operation uses a fresh decision and
key. Inventory key-ownership rejection is different: Work removes its provisional
attempt because that root belongs to another instruction. This permits a correct
legacy retry after a mistaken destination without poisoning the historical key.
A delayed rejected caller cannot clear a successor's pointer.

Revised tasks freeze their revision holdings. Unrevised legacy decisions retain
null expected contents rather than inventing a snapshot after a previous binary
already emptied the origin. Old completed tasks have no saved request fingerprint
and retain their historical same-key empty-movement replay. New successful intents
also replay task state without reporting another stock batch.

The cost is retained completion intents and an explicit recovery obligation for
ambiguous requests. The pending flag is a durable fence, not an expiry or a lease;
restarting a process or losing a database session cannot silently release it.

## 31. The demo owns disposable data; the platform owns its edge

A public demo is one shared warehouse with deliberately published human sign-in
credentials. Database, signing, webhook encryption and machine credentials remain
private and independent of any real deployment. The simulator builds operational
history through the same APIs visitors use. The first hosted rollout uses manual
resets; an automatic destructive schedule is deferred until operating needs
justify one.

Dokploy terminates HTTPS and injects domain/network configuration. Its standalone
Compose file reuses the existing demo services, while the web gateway listens only
on internal port80. A database reset must not replace those platform settings with
a raw Compose restart. Standalone deployments can restart directly; Dokploy resets
stop after database removal and require a platform redeploy.

Reset authority requires an explicit confirmation, exact Compose project identity
and a database volume marked as demo data. Arbitrary Compose project/file options
are refused. TLS state and logs are retained. Unmarked older volumes require
manual inspection instead of silent adoption. The cost is a visible outage and
operator action during reset; public DNS, certificate and routing verification
remain release requirements, not claims a local container test can establish.
