# Why Countflow is built this way

Eight decisions that would be expensive to reverse, each with the argument behind
it and **what it cost**. Where one turned out to be wrong, the correction sits
next to the claim rather than instead of it.

Source comments cite these as `ADR 0001` … `ADR 0008`; the numbers are the same.

1. [Inventory truth is an append-only ledger](#1-inventory-truth-is-an-append-only-ledger)
2. [A modular monolith, one schema per module](#2-a-modular-monolith-one-schema-per-module)
3. [Two React SPAs, not Blazor and not Next.js](#3-two-react-spas-not-blazor-and-not-nextjs)
4. [Every decision carries its reasoning](#4-every-decision-carries-its-reasoning)
5. [shadcn for behaviour, our own visual layer](#5-shadcn-for-behaviour-our-own-visual-layer)
6. [The simulator is a client, not a module](#6-the-simulator-is-a-client-not-a-module)
7. [The public surface is the surface](#7-the-public-surface-is-the-surface)
8. [The handheld writes to a durable queue](#8-the-handheld-writes-to-a-durable-queue)

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

## Adding a decision here

Add a section when a choice would be expensive to reverse and somebody will
otherwise undo it without knowing what it was for. Number it, add it to the list
at the top, and keep the shape: **the problem, the decision, and what it costs.**

A section with no cost stated is an advertisement rather than a decision record.
And when one of these turns out to be wrong, add the correction next to the claim
rather than editing the claim away — how a plausible belief survived is usually
the useful part.
