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
- **A label template on the floor is evidence, not a document.** Only a `Draft` can
  be edited, for the reason a rule set can only be edited as a draft — an `Active`
  template is what the building is printing and an `Archived` one is what every
  `LabelPrint` it produced still names, and that print is the system's only account
  of a physical object somebody may still be holding. Templates are never deleted.
- **A label template is validated by rendering it through the real renderer**, at
  the moment somebody presses save — the same trick, and the same argument, as
  building a rule set through the real catalogue. The specimen it renders against
  carries **every optional field absent**, because a pallet labelled before it was
  built is the layout's hardest case and its least likely test.
- **A field the kind does not publish is a hard failure; a field with no value
  prints as nothing.** The two are never collapsed. An unknown code is somebody
  renaming something and would otherwise print a pallet label with no batch number
  on it; an absent value is a mixed pallet having nothing honest to say, and the
  position stays occupied so the layout does not reflow.
- **A label never asserts what the ledger does not.** A pallet holding two SKUs has
  no SKU, no batch and no expiry on its label and no GS1 content block in its
  barcode — it says "mixed". A part-case pallet carries no AI 37, because the count
  is not an integer and rounding it puts a number in a barcode that a customer's
  system will believe.
- **Printing is recorded and is never a movement.** Nothing moved, so ADR 0001 keeps
  it out of the ledger for the reason a reservation and a clean count are kept out.
  It is recorded anyway, because a reprint means two physical objects each claiming
  to be one pallet. See [decision 13](decisions.md#13-a-label-kind-is-code-a-label-template-is-data).
- **An SSCC serial is allocated once and never reissued.** It is drawn from a
  PostgreSQL sequence precisely because `nextval` survives a rollback. A warehouse
  with no GS1 company prefix is refused rather than given a default — a made-up
  prefix is another company's, and the label scans. Exhaustion is a refusal, never a
  wrap. See [decision 14](decisions.md#14-countflow-issues-its-own-licence-plates).
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
- **A small field is the same trap from the narrow side, and the way out is an
  absolute scale.** Disposition ranks four fixed options and often two, so a
  field-relative normaliser would put its best at 1 and its worst at 0 on *every*
  decision rather than on an unlucky few — the Phase 5 counting flaw with the
  luck taken out. Every preference in that vocabulary therefore scores against a
  scale that exists without the field: fixed values per outcome, or a fraction of
  the item's own shelf life. The precedent is `putaway.consolidate_same_item`
  (1.0 / 0.8 / 0.35 / 0.0 against absolute categories since Phase 3), which is the
  one scoring rule of its kind that has never been one of the five. **Score
  against a scale that exists without the field**, and a degenerate field has
  nothing to be degenerate relative to. See
  [decision 12](decisions.md#12-a-disposition-is-a-verb-and-returns-belong-to-outbound).
- **`RuleInfluence` is near-useless over a four-element field, and that is known.**
  `UsesTheFullRange` fires on every categorical preference by construction and
  `DistinctValues` is 2 or 3 on every line. Do not "fix" the report to be quieter
  about disposition; it is telling the truth about a field too small to say much
  about. The instrument is for the vocabularies that rank hundreds.
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

- **Scheduled counting is opt-in and uses the same policy as manual counting.**
  A run key identifies one plan across retries, and every line retains its task
  enqueue key. Partial enqueues keep the run pending. Empty advice still records
  its decision trace. No scheduler posts a stock adjustment.
- **Manual planning, scheduled planning, and cancellation serialize per warehouse.**
  Open-holding exclusion and reference assignment run under the same session
  lock. Cancellation first resolves missing keyed enqueues, so a stale planner
  can only rediscover cancelled work after lock loss. Retrying a completed or
  cancelled plan cannot raise new work. Warehouse schedule runs retain a pending
  identity across partial failures, and reject stale saves through a revision.
  Each warehouse is dispatched independently. A persistent warehouse fence
  rotates with each planning write to reject stale policy results after lock loss.
  Disabling stops future plans while already pending work finishes.


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

### Returns and disposition

- **A return is two events, and the second one waits for a person.** Goods land in
  a **hold-role** bay at `OnHold` and an `Inspection` task is raised; the
  disposition runs on what the inspector found. Booking a return into racking is
  refused, and the condition alone would not save it — a bay that holds sellable
  stock is a bay somebody eventually releases this into by hand.
- **A disposition is a verb, not a `StockCondition`.** Restock, quarantine, repair
  and scrap each resolve to a movement the ledger already knew how to post —
  `StatusChange` then `Putaway`, `StatusChange` in place, `Transfer` to InTransit,
  `Scrap`. Wanting `StockCondition.AwaitingRepair` is the signal the two
  vocabularies have fused, not that the enum is too short. See
  [decision 12](decisions.md#12-a-disposition-is-a-verb-and-returns-belong-to-outbound).
- **The rules run on an override too, and the trace is written either way.** An
  override that skipped the evaluation would destroy the only record of what the
  system would have done, which is the one thing that makes it reviewable.
  `WasOverridden` is stored, not derived by comparing against the trace later —
  a rule set is promotable data and will have moved on.
- **Scrapping a return needs notes at the endpoint as well as at the ledger.**
  `ScrapInstruction.Notes` is `required` for a reason and the endpoint checks it
  first so the answer is a coded refusal rather than a five-hundred. Every other
  movement is explained by its document; a write-off is explained only by the
  person who ordered it.
- **`ReturnedQuantity` is summed over return lines, not over dispositions.** What
  an order wants to know is how much came back, which is settled the moment the
  goods are on the ledger. Counting dispositions would leave an order claiming
  nothing had come back while the pallet sat in the bay waiting for somebody.
  It is **not** capped at the ordered quantity and does not reduce
  `AllocatedQuantity`: a return is new supply, not an un-picking, and netting it
  off would make "what did we ship them" unanswerable.
- **Returns live in the `outbound` schema.** A return is inbound goods against an
  outbound document, and `SalesOrderLine.ReturnedQuantity` is a projection over
  its rows — owning it anywhere else would put that sum across a schema boundary
  (ADR 2) on the busiest page in the application.

### Labels and printing

- **A label kind is code and a label template is data**, and the line between them
  is `PalletLabelFields` — a binding contract, not a suggestion. Adding a field is a
  change to this repository, and that is the price of a template that can be checked
  before it is used ([decision 13](decisions.md#13-a-label-kind-is-code-a-label-template-is-data)).
- **The binder asks, the caller does not assemble.** `PalletLabelBinder` gathers a
  label's facts through four published contracts and no project references. A caller
  supplies the subject and the site; everything else it would have had to know —
  including that AI 02 is the *case* GTIN — lives in one place.
- **`Gs1ElementString` owns separator placement, and callers never concatenate AIs
  by hand.** A predefined-length element must not be followed by FNC1 and a variable
  one must be. Get it backwards and the symbol still prints, still scans, and
  decodes into the wrong fields.
- **Every value written into ZPL goes through the hex escape.** `^`, `~` and `\` are
  ZPL control characters, so a description reading `WIDGET ^ BRACKET` ends the field
  and runs the rest as commands — printing a label missing everything after the
  caret, without an error anywhere.
- **Only an active template may print; a preview may name a draft.** A preview also
  mints nothing and creates no licence plate: looking at a label is not making one.
- **Printing is the moment a licence plate becomes real.** Before labels a plate
  came into being as a side effect of posting stock; a printed label exists whether
  or not stock ever follows, so `ILicensePlateRegistry.EnsureAsync` is `Ensure` and
  not `Create` — a reprint lands on the plate the first print made.
- **Transport is not here and is not missing.** Generation is verified by asserting
  on the ZPL, with no printer. See [ROADMAP.md](../ROADMAP.md).

### The stock ledger

- **One posting path.** Every command goes through `StockLedger.PostAsync`. Adding
  a second way to write a movement removes the only place the invariants are
  enforced.
- **A `Receipt` may come from Supplier *or* Customer, and nothing else.** Phase 7's
  correction: until then a customer return credited the **supplier** account while
  the despatch that sent the goods out had debited the **customer** account, so
  the customer contra account only ever grew and could never be reconciled — the
  one account a distribution centre most wants to close, on the one ledger built
  to close it. `MovementType` is the movement's *shape*; which counterparty it
  came from is the business fact `MovementReason` carries. Do not widen it
  further: stock does not come back from having been destroyed, and an inbound
  correction is an `Adjustment` by name.
- **A `StatusChange` has to change the status.** Posting one that does not would
  move stock from a holding to itself — perfectly balanced, asserting nothing —
  and a history full of those is a history nobody can read.
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

- **Optional explanations are disclosed on demand.** Use the shared circled-i
  `InfoHelp` beside a heading or label. It opens on hover/focus, pins on click/tap,
  and dismisses on Escape, outside click, or a second click. Keep operational
  instructions, validation, task details, and queued/refused status visible.
  `Field.hintMode="instruction"` keeps instructional hints inline; the default
  hint mode is optional help. Returning from help must restore scanner focus
  without stealing focus from another input the operator chose.

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
- **Cardinal counts remain numbers.** Grammar-sensitive messages supply `_one`
  and `_other` forms in both catalogues; `translateCount` accepts known plural
  families and a finite numeric count. i18next uses the locale's `Intl.PluralRules`.
  Named server day/hour arguments are converted only for selection, without
  dropping their interpolation values. Formatting a number never changes its
  cardinal meaning; neutral totals use a separate display argument.
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
- **A pallet label's AI 02 is the case's GTIN, not the item's, and AI 37 counts
  cases and not base units.** The corollary of the GTIN note above, and the place it
  is most often got wrong: 48 base units of a 12-pack is four trade items.
- **Everything converts to base units at the edge.** Nothing downstream of
  `ResolveScan` should reason about packaging levels.
- Expiry tracking implies lot tracking: an expiry date belongs to a batch.


### Pick routing

- **Sequence the claimed membership.** Work locks picks before computing their
  walk; a tour never combines warehouses or demotes a priority band.
- **Completed stops cannot be released.** Completion and tour release serialize
  on task rows; completed tasks retain their physical confirmation.
- **Geometry belongs to the zone.** Midpoint uses the full rack, not just the
  stops. Save validates against the routing catalogue; claim records fallbacks.
- **Travel is a proxy.** Missing geometry means unknown cost; negative savings
  are preserved. Each tour stamps the requested and applied strategies.
- **Assigned work is never evicted by a history limit.** Released stops are
  pruned on authoritative refresh; unresolved confirmations remain protected.
- **A cached tour completion names its claim.** Completion validates the supplied
  tour id under the task lock; released or reassigned claims are refused.
- **Physical confirmations precede hand-back.** The handheld refuses to release
  a tour while its completions are pending, sending, refused, or stalled.


- **A recurring run is persisted before its handler starts.** Retries and restarts
  reuse its run ID and idempotency key until success is recorded. Each handler
  gets a fresh scope; write handlers deduplicate through owning-module commands.
  Session locks exclude healthy replicas, not side effects after a lost lock.
- **Job scheduling state belongs to Jobs.** A registration supplies code and
  deployment settings; Jobs never reads another module's business tables.
  Cancellation preserves unfinished work, failure backs off, and successful
  cadence resumes from completion without a catch-up burst.

Dispatch tracks each in-flight job independently, so a blocked handler does not
delay other schedules. Every attempt writes a fresh concurrency token before execution;
success and failure saves reject stale attempts after lock loss, preserving a
replacement run’s identity and due time.

### Replenishment

- **Min/max measures on-hand plus incoming.** Outbound reservations alone do not
  trigger classic min/max. The target never exceeds maximum or known capacity.
- **Bulk is the source, the configured face is the destination.** Allocation
  records its mandatory Reserve-role constraint; completion refuses another face
  even when the handheld is offline.
- **Reserve before raising work.** A durable intent and per-line keys converge
  after lost acknowledgements; competing faces cannot promise the same bulk stock.
- **Cancellation cannot release assigned stock.** Group withdrawal locks all
  tasks and refuses the whole group if any is assigned. Only withdrawn or terminal
  work permits release; named claims use atomic state predicates.
- **A replay consumes the same reservation.** A stock movement's retry key cannot
  acknowledge another reservation, even when its other posting fields match.
- **A shortage remains visible.** Partial top-ups keep their shortfall; no-source
  probes retain the decision trace without creating empty plans. Disabled policies
  still recover pending intent and preserve existing work.

### Webhooks

- **Persist before sending.** Cursor advance and immutable body are one
  Webhooks transaction. A pending or dead-lettered batch blocks later batches.
- **Retry exact bytes and identity.** A receiver must authenticate the raw body
  and deduplicate delivery IDs; neither a healthy lease nor HTTP 2xx gives an
  exactly-once guarantee after acknowledgement loss.
- **Verify before enabling.** Only a signed challenge echoed exactly as plain
  text activates a receiver. Pausing retains pending work.
- **A receiver cannot select internal services.** Public-address validation
  happens on the actual connection, with pinned DNS, no proxy, and no redirects.
  Internal origins require deployment configuration.
- **Secrets are never list fields.** Signing secrets are encrypted; inbound URL
  tokens are hashed. Capability request paths are excluded from logs and traces.
- **An inbound retry key identifies one request.** Revocation prevents new calls,
  while queued read-only reconciliation can finish independently.

### Operational dashboard

- **Throughput counts completion time.** A task created last month but completed
  today belongs to today's throughput; current backlog is not window-filtered.
- **UTC buckets preserve every day.** Today is partial; a zero completion total
  is measured, whereas an uncounted accuracy rate remains null.
- **Density counts storage locations, not mixed units.** Active bulk/pick faces
  are the denominator; inactive parent areas/zones are excluded. Zone IDs keep
  identical codes in different areas distinct.
- **Unknown cube utilization is never zero.** Every relevant capacity and stock
  volume must be known. Over-capacity measurements remain above 100 percent.
- **An update error stays visible.** Cached measurements retain their observation
  stamps; an independently refreshed dashboard is not a transactional snapshot.

### Handheld camera inspection

- **Camera input preserves scanner bytes.** GS1 group separators reach the existing
  resolver unchanged; a captured value never books stock or completes work.
- **One frame cannot choose between items.** Multiple distinct results require
  reframing; repeated detections of the same value count as one result.
- **The camera has an owner and an end.** There is one detection in flight; all
  tracks stop on capture, cancellation, backgrounding, failure or unmount. A late
  permission grant is stopped and cannot call the previous screen.
- **Unsupported and unanswered are visible.** Compatibility/permission refusals
  retain manual input. Offline capture and API errors do not invent a resolution.

### FIFO receipt age

- **Receipt age is a projection of physical history.** A receipt starts a cohort;
  internal moves, putaway, replenishment and status changes carry the source age.
  Partial draws preserve it; depletion clears it; a refill starts a new cohort.
- **A mixed holding retains its earliest known age until it empties.** Allocation
  chooses holdings, not individually identified receipt layers. Unknown-origin
  stock keeps age unknown, including when it mixes with known stock.
- **FIFO is an opt-in soft preference.** Known ages normalize oldest-first across
  the candidate field; unknown ages are neutral and explicitly explained. Hard
  constraints still exclude stock, and other configured preferences can trade off
  rotation. Existing active policies are not rewritten.
- **Replay carries the age at its checkpoint.** Today's balance age cannot replace
  the cohort that existed when the original decision was made.
- **The first upgrade is atomic and one-time.** The migrator locks the completion
  row and balances, streams the existing ledger, and updates only receipt-age
  fields. It never edits ledger entries or quantities. Drifted quantities receive
  no claimed age. A failed upgrade leaves its marker incomplete and can be retried.

## Blind bin counts

- **The job names a location, not an expected item.** BinCount tasks have no item,
  lot or pallet identity and quantity zero. Other task kinds still require an item
  and positive quantity. No expected contents appear in the blind task response.
- **A manual audit records its origin.** A supervisor's identity and mandatory
  reason travel in its source reference. This human-selected bin has no fabricated
  policy trace; existing rule-driven holding counts retain their trace requirement.
- **A confirmation covers the whole bin.** Separate item, batch, pallet and
  condition observations form holding identities. An empty list explicitly means
  empty; omitted positive book holdings become zero observations. Duplicate keys
  and invalid metadata refuse the entire batch.
- **Observation and correction are atomic.** Inventory locks balances, compares
  the bin's movement sequence, records agreements and posts all count variances
  through the same ledger transaction. New holding rows cannot slip into the union.
  Empty-bin audits have an explicit receipt even when there are no holding rows.
- **Changed stock requires a recount.** The version captured at claim/start must
  still match. Baseline capture drains in-flight stock writers before reading the
  version: sequence allocation order does not imply transaction commit order.
  Restart refreshes that version and the claim ID. Release/reclaim
  invalidates cached confirmations even when no stock moved.
- **Only the assigned operator confirms.** Bin confirmations require assignment,
  correct scanned location, and the current claim ID. Whole-bin observations remain
  durable in the operator's outbox until acknowledged by the server.
- **The receipt is immutable.** Same task, key and canonical payload replay the
  original result after later movements. Different payloads/keys refuse. Inventory
  commits before Work closes the job; recorded confirmations block release/restart
  until the saved confirmation finishes recovery.
- **Discovered quantities have unknown receipt age.** A count does not establish
  when goods arrived. The shared adjustment-age policy governs those additions.
