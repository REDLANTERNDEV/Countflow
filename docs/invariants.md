# Invariants

**Read this before changing anything.** Each rule prevents a class of bug that is
silent, expensive, or both, and most are enforced by a type, a database
constraint or a compile error rather than by review — where they are, it says so.
If you think one is wrong, open an issue rather than a pull request that has
already assumed the answer.

**On this page**

- [The rules](#the-rules) — what must never break, and why
- [Conventions](#conventions) — how code is laid out here
- [Working in a particular subsystem](#working-in-a-particular-subsystem) —
  [rules engine](#the-rules-engine) ·
  [integration surface](#the-integration-surface) ·
  [replay and the simulator](#replay-and-the-simulator) ·
  [cycle counting](#cycle-counting) ·
  [the handheld's offline queue](#the-handhelds-offline-queue) ·
  [outbound loop](#the-outbound-loop) ·
  [stock ledger](#the-stock-ledger) ·
  [design system and localisation](#the-design-system-and-localisation)
- [Domain notes](#domain-notes-worth-keeping-straight) — vocabulary that is easy to get subtly wrong

---

## The rules

- **The stock ledger is append-only.** Never `UPDATE` or `DELETE` a
  `StockMovement`. Corrections are compensating entries. Balances are a
  projection written in the same transaction, never the source of truth.
- **Every write command carries an idempotency key.** Against an append-only
  ledger a retried command that double-posts is a correctness bug. Handhelds
  retry constantly.
- **A reservation is not a movement.** A claim on stock asserts nothing physical —
  the pallet has not moved and a stocktake would find no trace of it — so it is
  never a ledger entry. `StockReservation` lives in the `inventory` schema anyway,
  because a pick has to move the goods and discharge the promise in one
  transaction and two schemas cannot share one. Available-to-promise is computed
  as on hand minus open claims, never stored: a second number is a second number
  to drift.
- **The one door onto reserved stock is `PickAsync`.** A pick names the
  reservation it discharges rather than an item, a quantity and a location, so
  picking stock nobody promised, picking more than was promised, and picking from
  a holding the rules did not choose are all unexpressible rather than merely
  checked for.
- **A count is not a movement.** A count that finds what was expected — the
  common case, and the valuable one — asserts that nothing happened, so it posts
  no ledger entry at all. `StockCount` lives in the `inventory` schema for the
  reason `StockReservation` does: a count that *does* find a variance must write
  the finding and post the `CountAdjustment` in one transaction, and two schemas
  cannot share one. Accuracy is verified over counted; a system that recorded
  only the counts that went wrong would know its numerator and have to guess at
  its denominator.
- **A count's variance is computed by Inventory, never supplied.** The caller
  says what was found; the ledger reads what it believed and subtracts. There is
  no way to ask for an adjustment of a particular size, which is what keeps the
  count evidence for the correction rather than decoration on it.
- **A rule evaluation cannot return a result without a decision trace.** Enforced
  in the type: `RuleEngine.Evaluate` is the only thing that can construct a
  `Decision<T>` (its constructor is `internal`), and a `Decision` always carries
  every candidate, the constraint that excluded each loser and the scored
  breakdown behind each survivor. There is no cheaper path to route around.
- **A directed movement carries its trace.** `PutawayInstruction.DecisionTraceId`
  is a `required Guid`, not a `Guid?` — the published contract cannot express an
  unexplained putaway. The ledger column stays nullable because ad-hoc transfers
  and corrections legitimately have no decision behind them.
- **A credential belongs to exactly one kind of principal.** A service account is
  created with no password hash at all and is refused by `LoginAsync` on top of
  that; a human cannot be issued an `ApiKey`. That is what makes "revoke every
  machine credential" and "make everyone change their password" two separate,
  complete operations rather than two thirds of one.
- **There is one authorization vocabulary, and it is roles.** An API key carries
  its account's roles and is judged by the identical `RequireRole` a person is
  judged by. Scopes beside roles are two sets of rules about one question, and the
  second set is the one nobody updates. The price is stated in [decision 7](decisions.md#7-the-public-surface-is-the-surface) and is not
  to be quietly paid off later.
- **The public surface is the surface.** All 58 paths under `/api/v1`, no private
  sibling, no curated parallel API. The console, the handheld and the simulator
  are clients of exactly these endpoints, and the OpenAPI document is regenerated
  by CI and fails on drift — so an undocumented change breaks our build before it
  breaks an integrator's. See [decision 7](decisions.md#7-the-public-surface-is-the-surface).
- **The change feed is a read, and stays one.** `/inventory/changes` is a cursor
  over `StockMovement.Sequence`. The pressure a public API applies is toward a
  parallel write path for integrators; Inventory has exactly one posting path, and
  it is to stay exactly one.
- **Modules never touch another module's tables.** Cross-module access goes
  through the published C# interface. Each module owns one Postgres schema.
  The contracts live in `SharedKernel/Contracts` — a consumer references the
  interface without referencing the implementation, so a boundary violation is a
  compile error rather than a review catch. `Countflow.Modules.Inventory` has no
  project reference to MasterData and must not gain one.
- **Migrations run in the migrator, never on API startup.**
- **A rule set on the floor is evidence, not a document.** Only a `Draft` can be
  edited. An `Active` set is what the warehouse is running and an `Archived` one is
  what every trace it ever produced points at, so editing either would rewrite the
  explanation behind pallets that have already been moved. Changing a live rule is
  clone → edit → replay → promote, and every one of those is a step somebody should
  be taking. Rule sets are never deleted.
- **A rule set is validated by building it through the real catalogue**, at the
  moment somebody presses save. Not by a second set of validation rules written
  beside the catalogue — those drift — and not at the next dock receipt, which is
  where an unvalidated set fails: at a rack, in front of an operator, with a receipt
  half done.
- **Every write the handheld makes goes through the durable queue, online or
  not.** There is no offline branch in any screen. A queue reached only when
  `navigator.onLine` is false has never run on the happy path, and a confirmation
  that goes straight to `fetch` is lost if the app dies between the tap and the
  response. One path costs a millisecond of IndexedDB and makes "the command
  survives" structural. See [decision 8](decisions.md#8-the-handheld-writes-to-a-durable-queue).
- **A queued command's idempotency key is its primary key**, not a field beside
  the record. A key regenerated on restore turns a replay into a second command,
  and against an append-only ledger that is a pallet booked away twice. Making
  the key the identity means there is nowhere in the restore path to mint one.
- **Only assertions are queueable; decisions are not.** "These goods came off a
  truck" and "I confirmed this job here" are things an operator may state with no
  network. "Give me the next job" is a request for a server decision — one queue,
  `FOR UPDATE SKIP LOCKED` — so a device that answered it locally would be
  inventing work. A claimed task can be worked offline **if the device already
  has it, and only then**.
- **A queued command belongs to the operator who made it and to nobody else.**
  The ledger records the credential that posted a movement, so replaying OP001's
  putaway while OP002 is signed in would write a name into an append-only record
  against work that person never did. Commands carry their owner, only the
  signed-in operator's are sent, and the rest wait visibly — including on the
  sign-in screen, where the person who *can* send them will be standing.
- **Optimistic state is never dressed as success, and a refusal is never
  dropped.** A held confirmation reads "saved on this device", in warning tone. A
  refused one is kept until acknowledged and shown as what-you-did /
  what-happened / what-to-do; a pending one cannot be cleared from the handheld
  at all, because it is the device's record of a pallet somebody moved.
- **Replay re-runs both rule sets; it never diffs two stored traces.**
  `TraceRetention` keeps a bounded sample of the *losing* field spread across
  reasons, so a stored trace cannot say where an arbitrary candidate ranked. The
  baseline re-run also checks the reconstruction: if the rules that made a decision
  no longer choose what they chose, the comparison is `Approximate` and says so.
  **Fidelity is measured, never asserted.**

---

## Conventions

- **Endpoints map paths relative to the published surface.** Write
  `MapGet("/inventory/movements")`, never `MapGet("/api/v1/inventory/movements")`.
  `MapCountflowModules` hands every module a route group already mounted under
  `ApiSurface.Prefix`, so the version is the host's business and no endpoint can
  sit outside it. Standing up a v2 is a second call there, not an edit to fifty
  files.
- Vertical slices: `Features/<UseCase>/` holds the endpoint, command, handler and
  validator together. Not layered folders.
- **A request contract's required fields are exactly its non-defaulted
  constructor parameters, and the binder now enforces that.** So an optional
  parameter must carry a default — `string? Notes = null`, never a bare
  `string? Notes` — or the host will require callers to send it, and the published
  document will say so. Two serializer options in `Program.cs` are what make the
  contract mean what it reads as; see
  [gotchas.md](gotchas.md) for what they are and what went wrong without them.
- Domain failures return `Result` / `Result<T>` with a coded `Error`; exceptions
  are for genuinely exceptional conditions. `ToHttpResult()` maps them.
- Add a module by adding one line to `ModuleCatalog.All` — the API and the
  migrator both compose from it, so they cannot drift.
- Package versions live in `backend/Directory.Packages.props` and the
  `frontend/pnpm-workspace.yaml` catalog. Never inline a version in a project file.

---

## Working in a particular subsystem

The rules above hold everywhere. These are the ones that only bite once you are
inside a given corner of the system, and every one of them was learned the
expensive way.

### The rules engine

- **Rule *configuration* is data; rule *kinds* are code.** A row supplies order,
  weight, enablement and parameters. Inventing a new judgement needs a class and
  a line in `PutawayRuleCatalogue`. An unrecognised rule code is fatal, never
  skipped — silently dropping a constraint is how a chilled pallet ends up in an
  ambient aisle because somebody renamed a class.
- **Constraint order is what the operator reads.** Evaluation stops at the first
  failure, so the reason shown is the one the rule author ranked highest. The
  same candidate under a different order gives a different (equally true)
  explanation; there is a test for exactly that.
- **Preferences return `[0, 1]` and the engine clamps.** Normalising is what makes
  weights comparable between rules measuring different things, and clamping is
  what stops one buggy rule silently outvoting the set.
- **The trace keeps a spread of exclusions, not a prefix.** Candidates arrive in
  warehouse order, so the first thirty rejections are thirty dock doors failing
  the same constraint. `RuleEngine.Sample` round-robins across distinct reasons.
- **A vocabulary is typed against its own candidate and context.** Putaway rules
  are written about `StorageLocationSnapshot`, allocation rules about
  `StockHolding`; neither can appear in the other's rule set, and that is a
  compile error rather than a review catch. The price is a second catalogue —
  a switch statement — and it is the right price.
- **A candidate key has to identify a candidate.** One bay holds good stock and
  quarantined stock of the same SKU, so an allocation key is location, batch and
  condition. Two rows with the same key in a trace is worse than a long key.
- **FEFO scores age as a fraction of the item's own shelf life, not against the
  other candidates**, and that is deliberate twice over. A relative window is
  anchored by every batch the constraints go on to refuse, so one unshippable
  short-dated pallet flattens rotation's influence over the batches that can go.
  And absolute is the truer statement: sixty days apart on a two-year life is
  nearly nothing and travel should break the tie, while the same sixty days on a
  three-week life is everything and rotation should win outright. Both fall out of
  the fraction automatically. Do not "fix" it back to a ranking.
- **Normalise a preference against the field it is comparing.** Putaway measures
  travel against the whole warehouse because it is choosing among two thousand
  bins; allocation measures it across the handful of holdings the SKU is actually
  in, because measuring four bins in one aisle against a twenty-four-aisle hall
  scores them identically and says nothing. Degenerate fields — one candidate, or
  all at one position — must score *best*, not worst: a lone option reported as
  "100% along the route" with a zero contribution is how people stop believing
  the numbers.
- **Counting measures travel against the building, like putaway — not against the
  field, like allocation.** It *looks* like allocation (a short list of holdings)
  and is really putaway: it chooses among the whole warehouse, and the list is
  short only because the constraints have already refused most of it. Phase 5
  shipped the field-normalised version and the container run caught it — four
  holdings in two adjacent bays scored 0% and 100% along the route, so a few
  metres took the full width of the score and travel (weight 0.15) outvoted
  exposure outright, reversing the ranking. The question is not "field or
  building" but **is the candidate list the thing being chosen among, or merely
  what survived the choosing?**

  The regression test for it took two attempts to make bite, because two
  neighbouring bays do *not* discriminate between the two normalisers and a
  **lone** candidate does. [The story is in the history](history.md#phase-5-cycle-counting-and-the-accuracy-dashboard);
  the rule it produced is the one above.

- **Travel is normalised by `RouteScale`, and the obvious composite is wrong.**
  A route position is naturally `zone × 1,000,000 + position`; dividing that by the
  building's largest composite is the fourth normalisation bug this system has had.
  Zone pick sequences are sparse and arbitrary (0, 10, 20, 90, 99, 100 here), so
  every bin in the ambient hall came out within one part in a hundred thousand of
  every other and `putaway.travel_distance` — weighted 0.25 — realised 0.8% of the
  ranking. It was not breaking ties; it could not express one. `RouteScale` ranks
  zones *ordinally*, gives each an equal slice of `[0, 1]` and spreads positions
  across their slice. Do not go back to dividing the composite.
- **`RouteScale` measures between the ends it actually has — the fifth instance,
  and it hid inside the fix for the fourth.** Ranking zones ordinally was right and
  incomplete: the scale still divided a position by the *largest* position rather
  than re-basing to its own minimum. For a scale built from the whole building that
  is invisible, because there is always something at the front and the nearest
  really is near zero. For a scale built from a **handful of candidates** it is the
  original disease: four holdings deep in the ambient hall all divide out to
  0.9998-something, giving a candidate-field spread of 0.0016 where the type's own
  documentation promises the nearest scores 0 and the furthest scores 1. Phase 7
  found it, and found it by way of the acceptance test that was *supposed* to
  reproduce the Phase 5 flaw and had quietly stopped doing so. Normalise
  `(position − nearest) / (furthest − nearest)`, never `position / furthest`.
- **Check a weight against what it actually did, not against what it says.**
  `RuleInfluence` is the instrument: a rule's worth to a ranking is its weight
  times how far apart it scored the candidates in front of it. Four scoring flaws
  so far, all the same shape, all a single line of that report. It needs no new
  storage — the scored breakdown is already on every trace — so it works
  retrospectively over history nobody thought to instrument.
- **Its findings are hints, not verdicts.** `allocation.travel_distance` is flagged
  as using the whole score range on most decisions and that is *correct*: allocation
  normalises against its candidate field on purpose. The report names the rule and
  shows the arithmetic; whether that is right is a person's judgement.

### The integration surface

- **Adding an endpoint is publishing a contract.** There is no internal API to
  hide behind ([decision 7](decisions.md#7-the-public-surface-is-the-surface)), so anything mapped is something an integrator may build
  on and `docs/api/versioning.md` promises not to break inside v1. That is the
  cost of the decision and the reason it is worth having.
- **`docs/api/error-codes.md` is part of the surface.** A new coded `Error` that
  is not in that table is an undocumented failure mode. Adding a code is fine —
  the policy says new codes appear without notice — but leaving it out of the
  table is not.
- **The i18n catalogue is the other half of an error code.** The API returns
  `{ code, args }` and never prose, so a code with no entry in `en.ts` renders as
  `errors.unknown` to a user. Both catalogues, and `tr.ts` is typed against
  `en.ts` so a missing key fails the build.
- **Give an integration the least role that covers its job.** A role is the
  smallest unit of authority a key can hold, so the account is where the scoping
  happens — one account per integration, not one account with everything.

### Replay and the simulator

- **The simulator is a client, not a module** ([decision 6](decisions.md#6-the-simulator-is-a-client-not-a-module)). `Countflow.Simulator` has
  no project references at all, so it cannot cross a boundary even by accident, and
  every state it reaches is one a real operator could have reached. A module would
  have needed project references to Inbound, Outbound and Counting, none of which
  publishes anything.
- **It has no top-level statements.** Those generate a `Program` in the global
  namespace and the integration tests resolve the API host through
  `WebApplicationFactory<Program>`; a second one makes that ambiguous. The migrator
  is kept out of the test project for the same reason — the simulator has to be in
  it, so it gives up its generated entry point instead.
- **The simulator rewrites the counting policy for its warehouse.** It clones the
  cycle-count set with `quietPeriodHours: 0`, scopes it to its own warehouse and
  promotes it, because a warehouse built ninety seconds ago has nothing countable.
  A simulated warehouse is therefore running a *different* counting policy from the
  shipped default. `--keep-quiet-period` turns it off.
- **`IStockHistory.WalkAsync` folds the ledger forward once** and pauses at each
  checkpoint. Summing the ledger per decision is quadratic in history: fine on a
  seeded database, fatal on a real quarter. Checkpoints must be ascending; the fold
  only goes forwards.
- **The same seed builds the same warehouse.** A guard that finds a scoring flaw
  one run in five is a guard people disable.
- **Replay filters traces to a warehouse in memory.** `InputsJson` is `jsonb` and
  Postgres has no `LIKE` for it, so the time window is what bounds the read.

### Cycle counting

- **A count task is a holding, not a bay.** One bin holding two SKUs is two
  counts, the same way a mixed pallet was three transfers in Phase 2. The price
  is that a count cannot discover an item the system has no record of in that
  bay at all — see the deferrals in [ROADMAP.md](../ROADMAP.md); `CountAsync` can express it,
  nothing raises it yet.
- **A count has nothing to override.** Every other job records `WasOverridden`
  when the operator confirms somewhere other than the suggestion. A count's
  operator is not choosing a destination, so confirming at a different bay is a
  mis-scan and is *refused* rather than recorded — and their disagreement with
  the system is the variance, which says far more than a boolean could.
- **The book figure is hidden behind a button on the handheld.** A counter shown
  "the system says 40" will find forty, and the count becomes an expensive way of
  confirming what was already believed. It is available, because somebody staring
  at a suspicious variance deserves it, but they have to ask.
- **The quiet period means a new warehouse cannot count anything for four hours.**
  `count.settled_stock` is right — counting a bay mid-putaway measures the truck
  and manufactures variances that then have to be corrected back — but it does
  mean a fresh install, a demo, or a test has nothing admissible until stock has
  stood still. Tests age `StockBalance.UpdatedAt` rather than faking a clock every
  module would then have to agree about.
- **That same constraint is what makes the counting tests deterministic.** Every
  holding created seconds ago is invisible to the policy, so a test that settles
  one holding knows exactly what a plan will pick up, without the other tests'
  stock drifting into the answer.

### The handheld's offline queue

- **The driver is testable; durability is not.** `test/outbox.test.ts` covers what
  the queue sends, in what order, whose work it will not touch, and what it does
  with each of the four answers a request can have — against the in-memory store.
  It cannot cover "the command survives a battery change", which is a claim about
  IndexedDB and a killed process, and **a test asserting it against a memory
  store would be asserting a fiction**. That half is verified by using the
  product: `docker compose stop api`, confirm work, reload, restart the API.
- **Four outcomes, and the line between them is the design.** `accepted`,
  `refused` (an answer — terminal), `unwell` (an answer about the *server*),
  `unreachable` (no answer). 429 and 408 are on the "nobody answered" side: a
  device coming out of a dead aisle with fifteen commands is exactly what trips
  the rate limiter, and treating that as a refusal would throw the shift away.
- **Wording is part of the feature.** `queue.advice.*` tells an operator what to
  do about the goods in their hands. "Count A01-01-02-A and tell your supervisor"
  is an instruction; `inventory.insufficient_stock` is a shrug. A new coded
  `Error` that a handheld can meet needs a row in `refusals.ts` as well as in
  `docs/api/error-codes.md`, or it renders as "we do not understand the reason".
- **The service worker is unverifiable in the in-app browser** — service workers
  are disabled there, and a trivially valid one fails to register too. Test it in
  a real Chromium: first load installs; with a command pending a new build must
  install and *wait*; the moment the queue empties the page reloads onto it. And
  prove the shell cache by **stopping the web container**, not by emulating
  offline in the page — a controlling worker fetches from its own target and the
  page's network emulation does not reach it.

### The outbound loop

- **Release is convergent, not transactional.** Reserve, then record, then raise —
  three schemas, so no transaction spans them. A crash after reserving leaves a
  hold this module has no record of, and the next release lets it go before
  allocating (`FindOpenByOwnerAsync`, minus what the allocation rows claim). A
  crash after recording leaves a commitment with no job, and the next release
  raises it, because the picks are driven off the rows rather than off what the
  current pass created. Running release again is always safe and always moves the
  order closer to fully released.
- **Releasing an already-released order is the way to chase a shortfall**, not an
  error. Lines that are fully committed are skipped; the rest are re-evaluated
  against a warehouse that has since taken a delivery.
- **Losing a holding between advice and reservation is not a failure.** Another
  order took the last of it in the intervening microseconds; the line goes short,
  which is what happened. Only `insufficient_available_stock` and
  `holding_not_found` are treated that way — everything else is a defect and
  surfaces as one.
- **Despatch is built from what was picked**, never from what was ordered or even
  from what was allocated. A line the rules could not fill and a pick nobody
  confirmed are both absent from the trailer, and a despatch built from intentions
  is how a customer is invoiced for stock still in the racking.

### The stock ledger

- **One posting path.** Every command goes through `StockLedger.PostAsync`. Adding
  a second way to write a movement removes the only place the invariants are
  enforced.
- **Locks are taken in `Holding.LockOrderKey` order**, which is what stops two
  opposite transfers deadlocking. Anything that touches more than one balance row
  must sort first.
- **`PostBatchAsync` is the posting path; `PostAsync` wraps it.** Phase 3 made the
  ledger able to carry several movements in one transaction, because a pallet
  that is half moved is worse than one that is not moved at all. Every balance
  row the batch touches is locked up front in one global order — the holding key
  extended with item and lot, a *suffix*, so a batch and a single command racing
  each other still agree on the order.
- **Sufficiency is checked against running quantities, not the locked snapshot.**
  Two lines of one pallet move can draw on the same balance row, and the second
  must see what the first took.
- **A whole-plate move recognises its key family before reading the plate.** The
  retry arrives *after* the pallet moved, so reading the source location would
  find it empty and report an error for work that succeeded.
- **A pick takes stock off its licence plate.** A putaway moves the pallet, so the
  plate travels; a pick breaks into it, so the destination holding has no plate.
  A whole-pallet pick therefore empties the plate, which is the honest description
  of what happened to it at the bench.
- **A reservation is consumed inside the posting transaction**, guarded on it
  still being open. Two confirmations of one pick both get past any check made
  beforehand; the second one's `UPDATE` matches no row and takes the batch down
  with it, which is the answer — the claim was already spent.
- **Reservations lock balance rows in `BalanceKey.LockOrderKey` order**, the same
  order the ledger takes them in, and take the reservation row *after* the
  balance. Both paths touch both tables; disagreeing about the order is a
  deadlock the moment the warehouse gets busy.

### The design system and localisation

- **shadcn is used for Radix behaviour only.** Every visual token is ours; no
  stock shadcn variable survives. Reach for a native element when it genuinely
  does the job (filter selects) rather than a Radix equivalent.
- **`Code` vs `Numeric` is a real distinction, not styling.** Identifiers (GTIN,
  SSCC, SKU, location code) are never grouped or locale-formatted. Values
  (quantities, weights, dates) always are. A number-formatted GTIN is not a
  barcode.
- **Density comes from `data-density`**, not per-app stylesheets. `console` = 30px
  controls and `handheld` = 52px, both set on `<html>`. `comfortable` = 44px is
  the one that is scoped to a container instead, for a focused single-task screen
  inside the console — the console scale is sized to fit table rows, and a screen
  holding two fields is not a table. The console's sign-in uses it. The drawer and
  its opening control are a separate documented exception at 44px: they exist only
  below `--breakpoint-lg`, where the console is being touched rather than pointed
  at, and the console scale is sized for a mouse.
- **A component names a type *role*, never a step.** `--control-text`,
  `--control-text-sm` and `--label-text` are what a control and its label wear;
  each density points them at the step that suits its own control height. A
  component that reaches for `--text-sm` directly gets the same 13px whether it
  is 30px tall or 52px, which is how the handheld spent its life with desktop
  type inside gloved-tap controls — heights scaled with density and the type did
  not. Steps (`--text-xs` … `--text-2xl`) are for prose and headings; controls
  take the roles.
- **The console's navigation is `NAV_GROUPS`, and adding a route means adding it
  there.** The rail and the drawer both render that one list, so a route the
  router knows about and the list does not is a page reachable only by typing its
  URL. Checked both ways by `apps/console/test/navigation.test.mjs`, which also
  holds `RAIL_BREAKPOINT` to the `--breakpoint-lg` token that Tailwind builds
  `lg:` from — the layout has to close the drawer when a rotating tablet crosses
  that width, and it cannot read the token at runtime (see [gotchas.md](gotchas.md)).
- **The API returns `{ code, args }`, never prose.** Wording lives in
  `packages/i18n`. Adding a language is a catalogue, not a backend change.
- **`tr.ts` is typed against `en.ts`**, so a missing key fails the build; the
  parity test additionally catches renamed interpolation placeholders.
- **Tailwind v4 does not cross package boundaries.** The `@source` in
  `packages/ui/src/theme.css` is what makes shared components get styles at all.
- **`launchSettings.json` cannot contain comments** (unlike `appsettings.json`).
  One silently breaks the Development profile and the OpenAPI export with it.
- **The OpenAPI export clears `servers`.** It would otherwise embed the ephemeral
  bound port and the CI drift check could never pass.

---

**The palette, from `countflow-logopack/`.** The mark is two interlocking arcs —
Espresso over Terracotta.

| | Hex | Use |
|---|---|---|
| Espresso | `#241C1A` | Text, primary mark, dark surface |
| Terracotta | `#C2542F` | Accent, action buttons, data |
| Bone | `#F3EDE6` | Surface |
| Clay | `#8A7C74` | Secondary text |

**Two adjustments were necessary and are deliberate** — see [decision 5](decisions.md#5-shadcn-for-behaviour-our-own-visual-layer):

- **Terracotta is not an accessible text colour.** 3.93:1 on Bone, 3.66:1 on
  Espresso — both under AA. It is used for *fills* (white on terracotta is
  4.56:1) and the mark; shaded/tinted variants carry link and label text.
  Tokens: `--color-accent` (fill) vs `--color-accent-text`.

  **A filled control therefore takes the `-on` token, never the `-text` one**, and
  that is a rule with a scar: the primary button wore `--color-accent-text` on
  `--color-accent` — 1.34:1 in light, 1.50:1 in dark — so the console's main
  action was unreadable in both themes until somebody looked at it. `danger` had
  the same shape one line below, failing in dark only, which is why
  `--color-danger` (a text colour, lightened for dark) now has a separate fill
  triplet beside it. `packages/ui/test/contrast.test.mjs` derives the fill/foreground
  pairs from the components and fails under 4.5:1, so the next one is a red test
  rather than a red button.
- **Terracotta sits at hue 15°, where danger red normally lives.** Status colours
  were moved to clearly separate hue families, and every status badge carries a
  text label — colour is never the only signal.

`LogoSymbol` is inlined SVG rather than an `<img>` so the espresso arc becomes
bone on a dark surface via `currentColor`. The static assets follow the same
idea, and there are only two places they live:

- **`docs/assets/`** — `logo-horizontal.svg` (espresso ink) and
  `logo-horizontal-on-dark.svg` (bone ink). Both transparent, paired in the
  README with `<picture>` and `prefers-color-scheme`. Nothing else in the
  documentation ships an image.
- **`apps/*/public/`** — what each app serves: `favicon.svg`, `symbol.svg`,
  `logo-horizontal.svg`, `logo-horizontal-dark.svg`, and the handheld's PWA
  icons. Note the app's `-dark` file is the version with a **solid espresso
  panel**, which is correct there and only there: `#241C1A` is also the app's
  dark surface token, so the panel is invisible. On a page whose background is
  any other dark — GitHub's, for instance — it would draw a visible card, which
  is why the README uses the transparent one instead.

The full brand pack (stacked lockup, monochrome mark, print PDF) is design
source and deliberately not in the repository.

---

## Domain notes worth keeping straight

- **A GTIN identifies a packaging level, not an item.** One SKU has a different
  GTIN on each of Each / Inner / Case / Pallet. Scanning a case barcode is how
  receiving knows it is holding 12 units, not 1.
- **Zone codes are unique within an area, not globally.** Every DC has a "DOCK"
  zone in both receiving and shipping — look zones up by (area, zone).
- **Everything converts to base units at the edge.** Nothing downstream of
  `ResolveScan` should reason about packaging levels.
- Expiry tracking implies lot tracking: an expiry date belongs to a batch.
