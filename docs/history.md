# How Countflow was built

Nine phases, each closed by driving the containerised stack by hand rather than
by a green test suite. What each phase added, what it left undone, and **what
using the product found that the tests could not** — every phase from 4 onwards
caught at least one defect that way.

The rules that came out of it are in [invariants.md](invariants.md), the traps in
[gotchas.md](gotchas.md), what is still open in [ROADMAP.md](../ROADMAP.md).
★ marks the three product pillars.

| # | Phase | |
|---|---|---|
| 0 | Foundation — modules, auth, Docker, CI | ✅ |
| 1 | Master data, location topology, GS1 parser | ✅ |
| 1.5 | Design system, theming, en/tr localisation | ✅ |
| 2 | Inventory core — the stock ledger ★ | ✅ |
| 3 | Rules engine, inbound loop, handheld tasks ★ | ✅ |
| 4 | Outbound loop — the walking skeleton completed | ✅ |
| 5 | Cycle counting and the accuracy dashboard | ✅ |
| 6 | Shadow replay and the warehouse simulator ★ | ✅ |
| 7 | Public integration surface, hosted demo | ✅ |
| 8 | The handheld's offline command queue | ✅ |

The module inventory that used to live here is now
[architecture.md](architecture.md); phases 0 and 1 have no section of their own
because their output *is* that inventory.

---

## Phase 2 — the stock ledger

The heart of the product, and what everything downstream reads from.

### What was built

- `Modules/Inventory`, schema `inventory`, one line in `ModuleCatalog.All`.
- **`StockMovement`** — append-only, guarded three deep: init-only properties, a
  `SaveChangesAsync` override that refuses to persist a modified or deleted entry,
  and a statement-level Postgres trigger that rejects `UPDATE`, `DELETE` and
  `TRUNCATE` outright. Carries a database-assigned `Sequence`, because replay
  needs a total order and two movements can share a timestamp.
- **`StockBalance`** — the projection, keyed `(item, lot, location, LPN, status)`
  with `NULLS NOT DISTINCT`, written in the same transaction as its movement. An
  `AllowsNegative` column copied from the location type backs a `CHECK`
  constraint, so "a shelf can never go negative" is enforced by the database
  rather than remembered by the posting code.
- **`Lot`** (expiry, production date) and **`LicensePlate`** (SSCC, check-digit
  validated, nestable). An LPN deliberately has no location column: where it is,
  is where its balances are.
- **`StockLedger`** — the single posting path. Validates, detects idempotent
  replays, creates-or-locks both balance rows *in a globally consistent order*,
  checks sufficiency, then appends the movement and folds it into both balances.
- **`LedgerReplay`** — one `UNION ALL` serving both reconciliation and
  point-in-time enquiry, so the reconciler's arithmetic is exercised by every
  as-of test rather than only by itself.
- **`LedgerReconciler`** + `PeriodicReconciliation` — replays the whole ledger
  against the projection on a timer and on demand, and alarms on drift.
  Read-only: it reports, it never repairs.
- Commands: receipt, transfer, adjustment, status change, and **reversal** as a
  compensating entry. Adjustments and reversals are supervisor/admin only.
- Console **Stock** and **Ledger** screens, including as-of reconstruction and a
  "verify against the ledger" button that runs reconciliation from the UI.

### Deliberately deferred, and why

- **Whole-LPN moves that cascade to nested plates.** A pallet holding three SKUs
  is three transfer calls today. A composite command has to post several entries
  under one idempotency key, and the caller that needs it is Phase 3's putaway
  task — one command to one entry keeps the idempotency guarantee trivially
  airtight while the ledger is new. `LicensePlate.ParentLicensePlateId` is stored
  and ready.
- **`DecisionTraceId`** is on every movement and always null. The rules engine
  that populates it is Phase 3; after that, a *directed* movement without a trace
  is a bug.
- **Allocation and reservations.** A balance is on-hand, not available-to-promise.
  Reservation belongs with the outbound loop.
- **Despatch and scrap have movement types and shape rules but no endpoints.**
  They are Phase 4's writes; adding them now would be untested surface.

### Proven by test — 32 in `InventoryLedgerTests`

- Conservation across a randomised 120-step sequence, checked three ways: the
  ledger sums to zero, the projection matches a full replay, and both match a
  tally the test kept independently.
- Twenty concurrent transfers out of a bin holding exactly ten transfers' worth:
  ten succeed, ten are refused, the bin lands on zero and never goes negative.
- Opposite-direction transfers under load do not deadlock.
- A retried command — and eight simultaneous copies of one — post exactly once.
- A key reused for a *different* movement is refused rather than replayed.
- Point-in-time balances match a snapshot taken at that instant.
- `UPDATE`, `DELETE` (even matching zero rows) and `TRUNCATE` on the ledger are
  all rejected by the database; the context refuses a modified entry.
- A tampered balance is caught by reconciliation, with the ledger itself intact.

---

## Phase 3 — rules engine, inbound loop, handheld tasks

The second product pillar, and the first work the system directs on somebody's
behalf.

**Done when** a pallet can be received on the handheld, the system suggests a
putaway location, the operator confirms it, and the ledger shows both movements
with a decision trace explaining the suggestion. It does — proven by
`InboundLoopTests` and driven by hand through the containerised stack:

```
Putaway for SKU-10001 ×24 → WH1-STOR-AMB-A01-01-02-A   "Standard putaway" v1
  2,240 locations considered, 1,920 admissible, score 0.598
    +0.122  location is empty
    +0.250  C-class item in a reserve location
    +0.225  zone AMB, 10% along the route
    +0.001  this would fill 1% of the free space
  ruled out (one kept per reason)
    −  …DOCK-DOOR-00-00-D01   the goods are already standing here
    −  …DOCK-DOOR-00-00-D02   not a storage location — it is a dock
    −  …STOR-CHL-A01-01-01-A  Ambient goods cannot go in a Chilled zone
```

### What was built

- **`SharedKernel/Rules`** — the engine. `Decision<T>` *is* the trace: its
  constructor is `internal`, so `RuleEngine.Evaluate` is the only thing that can
  produce one, and holding one means holding every candidate, the constraint that
  excluded each loser and the scored breakdown behind each survivor. [decision 4](decisions.md#4-every-decision-carries-its-reasoning)'s
  "enforced in the type", literally.
- **`Modules/Rules`**, schema `rules` — `RuleSet`/`RuleDefinition` as versioned,
  promotable data; `DecisionTrace`/`DecisionTraceCandidate`; the putaway
  vocabulary (7 hard constraints, 4 weighted preferences); `PutawayAdvisor`; and
  a preview endpoint that runs the identical advisor with nothing posted.
- **`Modules/Work`**, schema `work` — `WorkTask` and `WorkTaskSource`. Claiming
  uses `FOR UPDATE SKIP LOCKED`, so thirty operators pressing "next job" at a
  shift start each get a different one with no retry loop.
- **`Modules/Inbound`**, schema `inbound` — `Asn`/`AsnLine`/`AsnReceipt`, and the
  dock receipt that posts the goods, asks the advisor and raises the putaway.
- **The composite whole-LPN move**, deferred from Phase 2 until a caller existed.
  `StockLedger.PostBatchAsync` locks every touched balance row in one global
  order and posts every line in one transaction; `PostAsync` is now a
  single-command wrapper over it rather than a separate path.
- **Handheld**: home → receive → task, with "why here?" showing the trace at the
  rack. **Console**: work queue, rule sets with preview, advice notes, and a
  trace column on the ledger itself.

### Three projections, one argument

`StockBalance` was the first; Phase 3 added two more, and all three exist for the
same reason ([decision 1](decisions.md#1-inventory-truth-is-an-append-only-ledger), one floor down each time). `AsnLine.ReceivedQuantity` and
`WorkTask.Quantity` are both derived from append-only rows — `AsnReceipt` and
`WorkTaskSource` — with a unique idempotency key. A running total incremented in
place cannot be made idempotent: a retried dock scan that adds its quantity twice
is invisible until somebody argues with a supplier about a delivery that was
never short.

### Boundaries held

Phase 3 was explicitly not allowed to extend the ledger, and did not. Inbound
moves a great deal of stock and contains no code that writes to it; Work
completes jobs the same way. Everything goes through `IStockPosting`, which is a
door onto `StockLedger.PostAsync` rather than a second one. The contracts publish
*operations* — `ReceiveAsync`, `PutawayAsync` — not `PostAsync(movementType,
reason, …)`, so a consumer cannot assemble a movement the shape rules were never
written for.

### Deliberately deferred, and why

- **Re-slotting a mixed pallet.** A trailer unloaded SKU by SKU onto one pallet
  is one job — the queue merges by licence plate — but the suggestion and trace
  stay as they were when the first SKU landed. Re-deciding for a pallet whose
  contents are still growing would rewrite an explanation somebody may already
  have been shown. Doing it properly needs a decision context over pallet
  contents, which is Phase 4-shaped.
- **Editing rule sets from the console.** They are data and the schema supports
  promotion (`Draft` → `Active` → `Archived`, one active per kind per warehouse
  enforced by a partial unique index), but the only writer today is the seeder.
  The read surface and the preview are there; the editor is not.
- **Rule sets for allocation, waving and task assignment.** `DecisionKind` names
  all four. Only `Putaway` has a vocabulary.
- **The handheld's offline command queue.** Still Phase 4+. Every write already
  carries a client-generated idempotency key, which is the hard half.

### Proven by test — 18 in `InboundLoopTests`, 10 in `RuleEngineTests`

- The full loop: received → suggested → confirmed → both movements in the ledger,
  the putaway carrying its `TaskId` and `DecisionTraceId`.
- The same pallet scanned twice is sent to the same place.
- A chilled item is never sent to an ambient aisle, and the trace names the
  constraint and both temperature classes.
- Goods with nowhere to go still raise a task and a trace that explains why.
- Confirming elsewhere is recorded as an override, not refused — and the movement
  still carries the trace that was offered.
- A retried confirmation posts once; a *second* confirmation under a new key is
  refused.
- Two operators claiming simultaneously never receive the same task.
- A mixed pallet is one task and moves in one transaction; a retried whole-pallet
  move replays rather than finding the pallet gone.
- A retried dock scan does not book the delivery in twice.
- Goods cannot be booked straight into racking.
- An operator cannot read the rule set but can always read the reasoning.
- In the engine: no winner without its scores, one reason per exclusion, reason
  changes with constraint order, out-of-range scores are clamped, ties break
  deterministically, and the trace bounds what it keeps without ever
  understating what it looked at.

---

## Phase 4 — the outbound loop

The half of the walking skeleton that turns stock back into a delivery, and the
phase where the Phase 3 machinery had to prove it was machinery rather than one
working feature.

**Done when** an order can be released, allocated by rule, picked on the handheld
against a directed pick list, packed and despatched — and the ledger balances to
zero across the whole round trip from Supplier to Customer. It does, proven by
`OutboundLoopTests` and driven by hand through the containerised stack:

```
Pick 120 × SKU-10001 from WH1-STOR-AMB-A01-01-02-A → WH1-PACK-PACK-BENCH-00-00-01
  "Standard allocation" v1 · SO-2026-1041#1
    +0.450  expires 2027-02-07, in 179 days
    +0.200  this would clear the last 120 here
    +0.062  in a reserve location
    +0.100  zone AMB, 0% along the route

#1 Receipt   120  WH1-VIRT-SUPPLIER            → WH1-RECV-DOCK-DOOR-00-00-D01
#2 Putaway   120  WH1-RECV-DOCK-DOOR-00-00-D01 → WH1-STOR-AMB-A01-01-02-A   trace
#5 Pick      120  WH1-STOR-AMB-A01-01-02-A     → WH1-PACK-PACK-BENCH-00-00-01 trace
#8 Despatch  120  WH1-PACK-PACK-BENCH-00-00-01 → WH1-VIRT-CUSTOMER
                                            sum over every holding: 0
```

### What was built

- **Reservations, beside the ledger and not inside it.** `StockReservation` lives
  in the `inventory` schema because a pick must move the goods and discharge the
  promise in one transaction — but it is emphatically not a ledger entry, because
  a claim asserts nothing physical. Available-to-promise is computed, never
  stored. The full argument is now in [decision 1](decisions.md#1-inventory-truth-is-an-append-only-ledger).
- **`DecisionKind.Allocation` has a vocabulary** — 6 constraints, 4 preferences,
  the same `RuleEngine`, the same `Decision<T>`, the same journal. FEFO and
  minimum remaining shelf life are the two rules that finally give
  `Item.MinimumRemainingShelfLifePercent` a reader. The structural surprise is in
  [decision 4](decisions.md#4-every-decision-carries-its-reasoning): one decision produces *several* lines, and the fill is a deterministic
  walk down the engine's ranking so that no judgement escapes the trace.
- **`Modules/Outbound`**, schema `outbound` — `SalesOrder`/`SalesOrderLine`,
  append-only `OrderLineAllocation`, `Wave`, and release / pack / despatch /
  cancel. Orchestration only: it takes a great deal of stock out of the building
  and contains no code that writes to the ledger.
- **Picks on the existing queue.** `WorkTaskKind.Pick` is raised by the release
  and confirmed by the same endpoint a putaway is. The one place Work knows the
  difference is four lines choosing which published operation to call.
- **Despatch through `PostBatchAsync`** — every line of an order in one
  transaction, because a shipment that is half despatched is worse than one that
  is not, and only the second is obvious.
- **Console**: an outbound screen with allocations, pick progress and "why this
  stock?"; on-hand / committed / available on the stock screen. **Handheld**: no
  new screen, which was the point.

### The fourth projection, and the same argument

`SalesOrderLine.AllocatedQuantity` is derived from `OrderLineAllocation` rows with
a unique release key, exactly as `AsnLine.ReceivedQuantity` and
`WorkTask.Quantity` are derived from theirs. Four tables now make the same
argument ([decision 1](decisions.md#1-inventory-truth-is-an-append-only-ledger), one floor down each time): a running total incremented in
place cannot be made idempotent, and a retried release that committed the same
pallet twice would be invisible until two pickers arrived at one bin.

### Boundaries held

Phase 4 was allowed to extend the ledger no more than Phase 3 was, and did not.
Outbound reaches Inventory only through `IStockPosting`, `IStockAvailability` and
`IStockReservations`; the contracts publish *operations* — `PickAsync`,
`DespatchAsync` — and a pick names the reservation it discharges rather than an
item and a quantity, so picking stock nobody promised is not expressible. The one
genuine widening is inside Inventory: `PostingCommand` gained
`ConsumesReservationId` so the movement and the claim settle together.

`IWorkQueue` grew two methods and the line stayed where it was. A raiser may now
ask what became of the work it added and withdraw work nobody has started —
both things it is answerable for — and still cannot claim or complete anything.

### Deliberately deferred, and why

- **Re-slotting a mixed pallet**, again. It was deferred out of Phase 3 as
  "Phase 4-shaped", and on inspection it is not: it wants a putaway decision over
  a pallet's whole contents, which is inbound polish and a third decision context,
  and it is unrelated to getting stock out of the door. It belongs with Phase 5's
  slotting work, where re-deciding placement is the subject rather than a detour.
- **Reservations do not follow stock.** An ad-hoc transfer out of a reserved
  holding leaves the claim standing against a bin that no longer holds the goods,
  and the pick then fails loudly with "insufficient stock". Loudly is the right
  failure — the alternative is silently re-allocating somebody's order to stock
  they were not promised — but a supervisor screen for stranded commitments is
  worth having.
- **A batch the customer named is a `Guid` nothing populates.** The constraint is
  written and tested through the rules; wiring `RequiredLotId` from an order line
  needs lot resolution at order entry, and an order is routinely taken before the
  batch exists.
- **`DecisionKind.Waving` and `TaskAssignment` still have no vocabulary.** A wave
  is a grouping a person makes. The reason to give waving rules is to replay them
  against a month of real orders, which is Phase 6's machinery.
- **Load and trailer confirmation.** Despatch posts to the Customer counterparty
  from the bench; a shipment entity that groups orders onto a vehicle is Phase 7's
  integration surface rather than this skeleton.
- **The handheld's offline command queue.** Still deferred. Every write carries a
  client-generated idempotency key, which remains the hard half.

### Proven by test — 20 in `OutboundLoopTests`

- The full loop: released → allocated → picked → packed → despatched, four
  movements, and every holding of the SKU summing to zero.
- Stock promised to one order cannot be promised to another, and the trace names
  the committed quantity rather than reporting an empty shelf.
- Two orders released at the same instant for the last pallet: one gets it, the
  other is short.
- FEFO takes the older batch; the fresher one is ranked and beaten rather than
  hidden.
- A short-dated batch is refused even though it has not expired — the constraint
  outranks the preference, which is the whole difference between the two.
- Quarantined stock and stock still on the dock are considered and excluded by
  name.
- One line filled from two holdings under one decision and one trace, the second
  giving up only what was still outstanding.
- A retried pick posts once; a second confirmation under a new key is refused; a
  retried despatch posts once and a second one is refused.
- A release repeated under one key commits the stock once.
- Packing with a pick still open is refused, and so is despatching before packing.
- Cancelling releases the holds and withdraws the picks — and cannot be done out
  from under an operator who is holding one.
- A preview answers the same question and commits nothing.
- A wave releases every order in it through the same path.

---

## Phase 5 — cycle counting and the accuracy dashboard

The first phase about the *quality* of the record rather than its extent, which
is what makes it the natural successor to the walking skeleton — an accuracy
claim nobody measures is a marketing claim.

**Done when** a count task is raised by policy, an operator counts it on the
handheld, a variance posts a `CountAdjustment` carrying its decision trace, and
the console shows accuracy by zone and class alongside override rates and
reconciliation drift. It does, proven by `CountingLoopTests` and driven by hand
through the containerised stack:

```
CNT-2026-0001  Open  raised 4/4
  #1 WH1-STOR-AMB-A01-02-01-A   book 80   #3 WH1-STOR-AMB-A01-01-01-A   book 60
  #2 WH1-STOR-AMB-A01-02-01-B   book 80   #4 WH1-STOR-AMB-A01-01-01-B   book 60

why A01-02-01-A first — "Standard cycle count" v1, 4 considered, 4 due
  +0.400  count.overdue          never counted
  +0.125  count.velocity         B-class item
  +0.057  count.stock_at_risk    80 here — 29% of everything we hold of it
  +0.135  count.travel_distance  zone AMB, 10% along the route

count A01-02-01-A   book 80  found 78  variance −2   → 1 movement
count A01-02-01-B   book 80  found 80  variance  0   → 0 movements, and that is the success

#17  CountAdjustment  2  WH1-STOR-AMB-A01-02-01-A → WH1-VIRT-ADJUSTMENT   trace
                                            sum over every holding: 0

accuracy   1/2 agreed   50.0%   net −2   absolute 2
           zone AMB 1/2 · class B 1/2
overrides  Putaway 6/0 · Pick 2/0 · CycleCount 2/0
```

### What was built

- **Counts, beside the ledger and not inside it.** `StockCount` lives in the
  `inventory` schema for the reason `StockReservation` does — a variance has to
  write the finding and post the correction together, and two schemas cannot
  share a transaction. But a count is emphatically not a movement: the common
  case finds what was expected and posts *nothing*, and that clean count is the
  one that matters most, because accuracy is verified over counted. A system
  recording only the counts that went wrong would know its numerator and have to
  guess at its denominator.
- **`DecisionKind.CycleCount` has a vocabulary** — 4 constraints, 4 preferences,
  the same `RuleEngine`, the same `Decision<T>`, the same journal, the same
  preview endpoint and the same console trace component. Adding it needed a
  context, two rule files, a catalogue switch and a seeder, and touched nothing
  else. That is the afternoon [decision 4](decisions.md#4-every-decision-carries-its-reasoning) promised, and the first real evidence the
  engine generalised rather than being written twice and got lucky.
- **`Modules/Counting`**, schema `counting` — `CountPlan`/`CountPlanLine` and a
  convergent planning path. Orchestration only: it raises a great many jobs and
  writes no stock figure of its own.
- **Counts on the existing queue.** `WorkTaskKind.CycleCount` is raised by the
  plan and confirmed by the same endpoint a putaway is. The handheld gained one
  field and no screen.
- **`MovementType.Scrap` got its endpoint**, which belongs in this phase because
  a write-off is what a count variance turns into once somebody has gone and
  looked at the missing stock and found it broken.
- **Console**: an accuracy screen composed from three modules' reads, and a
  counting screen with the policy's trace behind every plan.

### The task whose completion is not a movement

This was the phase's real question, and the answer was one nullable column.

Every job before a count confirms "I moved it to here", which
`CompletedAtLocationId` already recorded. A count confirms "I found this much" —
a different sentence with the same grammar, still the operator telling the system
what they did, still recorded beside the intention so the two can be compared.
`WorkTask.CountedQuantity` carries it and the completion switch grew a branch.

The queue, claim-next, the assignment rules, the override recording and the
handheld screen are all still written against "go here, tell me what happened",
which turns out to be the general shape rather than the putaway-shaped one. A
second task model would have bought a second queue, a second claim-next and a
second set of override statistics to carry one decimal.

Two consequences fell out and are worth keeping straight:

- **A count has nothing to override.** Confirming a putaway somewhere else is a
  judgement worth capturing; confirming a count somewhere else is an answer to a
  question nobody asked, so it is refused rather than recorded. The operator's
  disagreement with the system is the variance, which is a far better record than
  a boolean.
- **A count may post nought, or one.** `CompleteWorkTask` now hands back a
  confirmation rather than a bare list of movements, because an empty list is a
  success and needed something to say so.

### Boundaries held

Phase 5 was allowed to extend the ledger no more than Phases 3 and 4 were, and
did not. `IStockPosting` gained `CountAsync` and `ScrapAsync` — operations, not
`PostAsync(movementType, reason, …)` — and `CountAsync` deliberately takes what
was *found* rather than a variance, so Inventory reads what it believed and does
the subtraction. A caller cannot ask for an adjustment of a size it made up, which
is what keeps the count evidence for the correction rather than decoration on it.

The reconciler stayed a pure read of the ledger it audits. What Phase 5 added is
a journal beside it, written by the callers, so the dashboard reads what the
reconciler produced rather than working the same question out a second way — two
answers to "is the stock right" being exactly the problem it exists to prevent.

### The dashboard is three fetches, not one endpoint

Count accuracy and stranded commitments are Inventory's, override rates are
Work's, and no module may reach into another's schema ([decision 2](decisions.md#2-a-modular-monolith-one-schema-per-module)). So each answers
for what it owns and the console puts the three side by side. That is the honest
price of the boundary and it is a cheap one.

### Proven by test — 24 in `CountingLoopTests`

- The full loop: raised by policy → claimed → counted → variance posted with its
  trace → the ledger sums to zero → the accuracy read reports it.
- A count that agrees with the book posts nothing, and is still on the record.
- Finding *more* is not refused; finding nothing at all is an answer.
- A movement between planning and counting does not corrupt the variance — the
  correction is a delta, and the pick stays correctly in the ledger on its own
  account.
- A retried confirmation counts once; a second under a new key is refused; a
  retried *clean* count is recorded once, which no stock figure would have caught.
- A count with no quantity, at the wrong bay, or with a negative reading is
  refused, and the wrong bay is refused rather than recorded as an override.
- A bay being worked, a bay already on somebody's list and a bay counted this
  week are all considered and excluded *by name*.
- Travel is measured against the building rather than the candidate field.
- A plan converges when retried, withdraws cleanly, and cannot be withdrawn out
  from under an operator holding a count.
- A write-off posts to the Scrap counterparty and is refused without a reason.
- The reconciler's findings are kept and read back.
- A commitment left standing against stock that has moved shows up as stranded.

### What the container run caught, again

Phase 4 found two scoring flaws only visible in a warehouse with realistic stock
behind it. Phase 5 found a third, of exactly the same kind.

**Travel was normalised against the candidate field.** Four admissible holdings
in two adjacent bays came back scored 0% and 100% along the route — a few metres
stretched across the full width of the score — so travel contributed a 0.150
swing while the exposure rule it is meant to defer to contributed 0.014, and the
ranking came out backwards. Counting *looks* like allocation, a short list of
holdings, and is really putaway: it chooses among the whole building, and the
list is short only because the constraints have already refused most of it. It
now measures against the warehouse, as putaway does. Before: 0.718 / 0.582, wrong
order. After: 0.717 / 0.703, right order, travel as the tie-break it was meant to
be.

The regression test took two attempts to make bite, which is worth recording.
Two neighbouring bays do not discriminate — they share a route position, so the
field's span is zero and both normalisers agree. A *lone* candidate does: against
the field it is both nearest and furthest and scores a flat 1, against the
building it scores its real position. **Verify a guard of this kind by reverting
the fix and watching it fail.** The first version passed either way.

### Deliberately deferred, and why

- **A count is of a holding, not of a bay.** One bin holding two SKUs is two
  counts, the same way a mixed pallet was three transfers in Phase 2. The cost is
  that a count cannot discover an item the system has no record of in that bay at
  all — the blind bin count. `CountAsync` names the holding by its parts rather
  than by a balance row precisely so it *can* express "twelve cases in a bay we
  think is empty"; what is missing is a task shape that asks the question.
- **Re-slotting a mixed pallet**, a third time. It was moved out of Phase 3 as
  "Phase 4-shaped", out of Phase 4 as belonging with Phase 5's slotting work —
  and Phase 5 turned out not to have slotting work. It wants a putaway decision
  over a pallet's whole contents, which is inbound polish and a fourth decision
  context. It is not counting, and pretending otherwise has now cost three
  deferrals. It belongs on its own or not at all.
- **Nothing repairs a stranded commitment.** The screen surfaces them; releasing
  the claim and re-allocating is still a supervisor cancelling the order and
  releasing it again. A targeted "re-home this commitment" needs allocation to
  run for one line rather than one order.
- **Counting has no schedule.** A plan is raised by a supervisor pressing a
  button. A nightly job that raised the shift's counts is a `CronCreate` away and
  is deliberately not in this phase: the policy should be watched by a person for
  a week before it runs unattended.
- **`DecisionKind.Waving` and `TaskAssignment` still have no vocabulary.** The
  reason to give waving rules is to replay them against a month of real orders,
  which is Phase 6's machinery.
- **The handheld's offline command queue.** Still deferred, and every write still
  carries a client-generated idempotency key, which remains the hard half.

### A note for whoever runs this fresh

`count.settled_stock` will not let a bay be counted within four hours of anything
moving in it, which is correct — counting a bay mid-putaway measures the truck —
and means **a fresh install has nothing countable until stock has stood still**.
The tests age `StockBalance.UpdatedAt`; the container run above did the same with
one `UPDATE`. A demo needs the same or a rule set with the quiet period lowered,
which is a row, not a deploy.

---


## Phase 6 — shadow replay and the warehouse simulator

The third product pillar, and the one the other two exist to make possible. It is
impossible without either of them: without the ledger there is no way to rebuild
the warehouse a past decision saw, and without the engine there is no rule set to
hand different inputs to and run again.

**Done when** a candidate rule set can be replayed against real history and the
console shows what would have changed — which decisions differ, by how much, and
why — and a simulated warehouse can be run forward to produce the realistic
candidate fields three phases of container runs have been catching bugs in. It
does, proven by `ReplayTests`:

```
RPL-2026-0001  Putaway without velocity slotting  against Standard putaway v1
  35 replayed · 35 reproduced exactly · 15 changed · 11 reordered · 0 lost · 41 ms

what did the deciding                weight   asked   realised   spread
  putaway.velocity_slotting            0.25     25%        45%    0.547   ← outvotes its weight
  putaway.travel_distance              0.25     25%        24%    0.160
  putaway.consolidate_same_item        0.35     35%        23%    0.200
  putaway.cube_utilisation             0.15     15%         9%    0.089

  14:02:11  SIM-0007 ×96   Changed   A01-01-02-A → A03-04-01-A   was ranked #4
```

### What was built

- **`SharedKernel/Rules/RuleInfluence`** — the second instrument explainability
  needed, and the one that turned out to matter most. A trace answers "why this
  bin?" completely and cannot answer "which rule is actually deciding?" — which is
  the question behind every scoring flaw this system has had. A rule's worth to a
  ranking is its weight multiplied by how far apart it scores the candidates in
  front of it, and the second factor is invisible until there is a real warehouse
  behind the decision. It needed **nothing new stored**: every admissible
  candidate's scored breakdown was already in the trace.
- **`IStockHistory`** — Inventory's published read surface for the past, and the
  third place the immutable ledger pays for itself. It *folds the movement stream
  forward once*, pausing at each checkpoint, rather than summing the ledger per
  decision — linear in history and in decisions rather than quadratic, which is
  the difference between a replay somebody waits for and one they abandon.
- **`IShadowReplay`** in `Modules/Rules` — re-runs both rule sets over one
  reconstruction. See [decision 4](decisions.md#4-every-decision-carries-its-reasoning) for why it cannot be a diff of two stored traces,
  and why the baseline re-run doubles as a check on the reconstruction.
- **`Modules/Replay`**, schema `replay` — the experiment log. The fourth
  orchestrator of the shape Inbound, Outbound and Counting established.
- **`Countflow.Simulator`** — a client, not a module ([decision 6](decisions.md#6-the-simulator-is-a-client-not-a-module)). It signs in and
  books deliveries, does the jobs, ships the orders and counts the racking through
  the endpoints a handheld and a console use.
- **The rule-set editor**, deferred since Phase 3 and finally forced: replay
  compares a candidate against what ran, and without a way to author a candidate
  the machinery has nothing to say. Drafts only — an active set is what the floor
  is running on and an archived one is what a trace from last quarter points at,
  so both are evidence rather than documents. A set is validated by **building it
  through the real catalogue**, so an unusable rule set is refused at save rather
  than at a rack with a pallet on a forklift.
- **Console**: a replay screen showing the summary, the influence table and the
  per-decision diff, with the reproduction rate sitting beside the conclusions
  rather than under them.

### What the machine caught

Phase 6 was asked to build something that would have caught the three scoring
flaws found by hand in Phases 4 and 5. Pointed at a simulated warehouse for the
first time, it found **a fourth** that three phases had missed.

`putaway.travel_distance` is weighted 0.25 of the putaway rule set and was
realising **0.8% of the ranking**. The route position — `zone × 1,000,000 +
position` — was normalised by dividing it by the building's largest composite.
Zone pick sequences are sparse and arbitrary (0, 10, 20, 90, 99, 100 here), so
every bin in the ambient hall landed within one part in a hundred thousand of
every other. Travel was not breaking ties; it *could not express one*. The same
arithmetic made counting's travel rule inert — spread 0.019 across a whole hall.

`RouteScale` fixes it by ranking zones ordinally and giving each an equal slice of
`[0, 1]`, with positions spreading across their slice. Travel's realised share
went **0.008 → 0.237** against a nominal 0.25, which is what the rule set was
asking for all along. Four instances of the same failure now, and the first found
by a machine rather than by a person reading a trace at a rack.

The acceptance test then reproduces the Phase 5 flaw deliberately: the counting
travel rule's scale is a rule parameter, so the known-bad choice is writable,
replayable, and shown to be bad. Against a simulated warehouse it moves the
ranking and stretches travel's spread from 0.019 to 0.267 — and the same test
asserts the shipped rule set is *not* flagged, because a detector that fires on
everything is not a detector.

### The correction to [decision 1](decisions.md#1-inventory-truth-is-an-append-only-ledger)

Reservations turn out to be replayable, which this project has said since Phase 4
they were not. A claim carries `CreatedAt` and a set-once `ClosedAt`, its quantity
is immutable and its row is never deleted, so the claims standing at any instant
come straight out of the table. "Not append-only" was treated as equivalent to
"not reconstructible" and it is not — a mutable row recovers its own history
perfectly well when the mutation is monotone and timestamped.

Which is why fidelity is now **measured rather than asserted**. Every replay runs
the rules that made each decision over the rebuilt inputs and checks whether they
still choose what they chose. A claim in a document goes quietly out of date, as
this one did; a number the run publishes about itself cannot.

### Boundaries held

`Modules/Replay` reads Rules, Inventory history and MasterData and has a project
reference to none of them. The simulator has project references to *nothing at
all*, which is stronger than any module could manage and is the whole of [decision 6](decisions.md#6-the-simulator-is-a-client-not-a-module).

The one genuine widening is in Inventory, and it is a read: `IStockHistory` hands
out the past. It computes nothing the ledger does not already contain.

### Deliberately deferred, and why

- **`DecisionKind.Waving` and `TaskAssignment` still have no vocabulary.** The
  standing reason to give waving rules was "so it can be replayed against a month
  of real orders", and that reason has now been discharged — the machinery exists.
  The work is simply not done, and it is the first thing a Phase 6.5 would pick up.
- **A replay is synchronous.** A run of two hundred decisions takes tens of
  milliseconds because the engine is fast and the fold is linear, so a background
  job would be machinery in front of a problem nobody has. A year of history and a
  thousand decisions would change that.
- **Traces are filtered to a warehouse in memory.** `InputsJson` is `jsonb` and
  Postgres has no `LIKE` for it, so the window bounds the read and the warehouse
  is matched after parsing. A `WarehouseId` column on `DecisionTrace` would fix it
  and is a migration nobody has needed yet.
- **The console cannot yet author a rule set.** The editor is API-complete and
  tested; the screen shows rule sets and runs replays against them, and the
  drafting UI is not built. A supervisor can drive it through the API today, which
  is exactly what the simulator does.
- **Allocation's travel rule is flagged as field-relative and that is correct.**
  The influence report says "this rule uses the whole score range", which is true
  and deliberate for allocation. The findings are hints, not verdicts.
- **The handheld's offline command queue.** Still deferred, five phases running.

### Proven by test — 15 in `ReplayTests`, 9 in `RuleInfluenceTests`

- **The control, first:** a rule set replayed against itself reproduces every
  putaway exactly. Putaway is the strictest case — its inputs are occupancy, and
  occupancy at an instant is exactly what summing the ledger to that instant
  produces. If it is not 100%, the fold is wrong and nothing else in the file
  means anything.
- An allocation replay rebuilds the commitments standing at the time. It would
  fail if the reconstruction netted off today's, because by then every order has
  been picked and its claims closed.
- The field-normalised counting policy changes the ranking on a simulated
  warehouse, and the influence report names travel as the cause; the shipped set
  over the same decisions is not flagged.
- Changing a weight moves decisions, and the run reports where the new winner sat
  in the old ranking — the number a diff of two stored traces could never supply.
- A run is kept and reads back with what it said at the time, and can be narrowed
  to what changed.
- A rule set of the wrong kind is refused rather than run; an operator cannot run
  experiments at all.
- A rule set that could not run is refused when it is **saved**; a rule from
  another vocabulary cannot be put in a set; an active set cannot be edited;
  promotion archives what it replaces and takes the next version; a supervisor may
  draft and only an admin may promote.
- In the influence report: a rule's realised swing is its weight times how much it
  varied, a weight on its own predicts nothing, the heaviest rule is never accused
  of outvoting its weight, a field of one flags nothing, and the live and stored
  readings of the same decision agree.

---


## Phase 7 — the public integration surface and the hosted demo

The phase that turns a working warehouse into something somebody else can build
against, and the first one whose subject is a promise rather than a capability.

**Done when** an outside integrator can authenticate with a machine credential,
read stock and decisions, and drive the inbound and outbound loops against a
documented, versioned surface — and when there is a demo stack somebody can be
pointed at without a walkthrough. Both hold, and the acceptance test is the one
thing in this repository that consumes the API from outside:

```
Countflow simulator → http://localhost:8080
authenticated as supervisor (key cfk_x1VUck…), operator (key cfk_xFvQ9K…) and admin (key cfk_r75gWq…)
promoted 'Simulated cycle count (WH1)' v2 for WH1 with no quiet period
60 receipts, 60 putaways (2 overridden), 12 orders (0 lines short), 28 picks,
12 despatches, 4 count plans, 38 counts (5 with a variance)
```

No password anywhere in that run.

### Two decisions taken, and both held

- **The console rule-set editor belonged in Phase 7**, not a 6.5. It shipped, and
  it was the right call for a reason nobody predicted: the screen 500'd on its
  first Save, on a code path six phases of tests had never executed (below).
- **`DecisionKind.Waving` deferred again, on its merits.** Untouched, and the
  argument from the phase before still stands: the three existing vocabularies
  rank a field and pick a winner, which is what `Decision<T>` is; waving
  *partitions a set*, which is a question about the engine rather than a rule
  file, and not work to start in the phase that freezes the surface into a v1
  contract. `TaskAssignment` likewise.

### What was built

- **Machine credentials**, in `Modules/Identity`. An `ApiKey` belongs to a
  `CountflowUser` flagged `IsServiceAccount`, is hashed at rest with a **single
  SHA-256**, is shown once, and is revoked rather than deleted. It authenticates
  through a **second scheme** selected by an `AddPolicyScheme` forwarder on the
  presence of `X-Api-Key`, producing the same shape of principal the JWT handler
  produces — so every `RequireRole` already written on every endpoint covers an
  integration unchanged and **no second authorization vocabulary exists**.
- **The ledger change feed** — `GET /inventory/changes?afterSequence=&limit=`.
  Change data capture with no outbox, because `StockMovement.Sequence` was
  already a total order over every change inventory has ever undergone. A read,
  which is what keeps the public surface from becoming a seventh way to write a
  movement. The subtlety is the watermark; see below.
- **Rate limiting per credential**, partitioned on the credential a caller
  *presented* rather than the identity it turned out to belong to — which is why
  it runs before authentication, and why a flood of forged keys costs a SHA-256
  of a header rather than a database lookup each.
- **The console rule-set drafting screen**, deferred since Phase 3. Clone → edit
  → promote, with the vocabulary for that decision kind and no other, weights
  absent on constraints rather than greyed out, and promotion disabled with a
  reason for a supervisor.
- **An integrations screen** — service accounts, their roles, their keys, the
  one-time secret panel, and revocation.
- **A tour route and a demo-only landing panel**, because "without a walkthrough"
  means the application has to explain itself. The sign-in hints come from a Vite
  build argument, so a production build has no way to grow them.
- **`docs/api/`** — authentication, versioning and compatibility, idempotency,
  pagination and the change feed, the full error-code table, and both loops end
  to end with worked requests. Plus real OpenAPI `info` and security metadata,
  and **[decision 7](decisions.md#7-the-public-surface-is-the-surface)**.
- **`compose.demo.yaml`, `docker/simulator.Dockerfile` and `scripts/demo.sh`** —
  one command, secrets generated on first run, and a demo warehouse **built by a
  client through the public API** rather than by a seeder. [decision 6](decisions.md#6-the-simulator-is-a-client-not-a-module) made literal.

### 58 paths, and no private sibling

Four new: `POST`/`GET /auth/service-accounts`, `POST`/`GET
/auth/service-accounts/{id}/keys`, `POST /auth/service-accounts/keys/{id}/revoke`,
and `GET /inventory/changes`.

The argument for one surface rather than a curated public one is [decision 7](decisions.md#7-the-public-surface-is-the-surface), and it
comes down to what a parallel surface would have had to reference: Inbound,
Outbound and Counting publish *nothing*, so an integration module would have
needed project references to three implementations and become the first thing in
the codebase permitted to ignore [decision 2](decisions.md#2-a-modular-monolith-one-schema-per-module) — in the phase whose subject was
stability. The stability an integrator gets comes from a written policy and a CI
drift check instead.

### Two things that were nearly wrong, and one that was

**The change feed's watermark is the endpoint's correctness, not a tuning knob.**
PostgreSQL assigns an identity value at `INSERT`, not at `COMMIT`, so two
concurrent postings can take 4470 and 4471 and commit the other way round. A
reader served 4471 immediately would advance past 4470 and never be shown it — a
consumer's projection diverging silently from a ledger that is itself perfectly
correct, which is the same class of bug as a double-posted retry and just as
invisible. The feed therefore serves only up to the highest sequence whose
`OccurredAt` is at least five seconds old. Note it filters on **sequence**, not on
the timestamp: filtering on the timestamp reintroduces the same hole from the
other side, because the row that lost the race can carry the later one.

**The limiter partitions on the presented credential, not the authenticated one.**
Partitioning on identity means every request — including a flood of forged keys —
pays for a database lookup before anything decides whether to serve it, which
turns the credential check into the denial-of-service vector the single-SHA-256
decision was made to avoid.

**A service account's operator code was a slice of a UUIDv7, and collided
immediately.** A v7's leading hex digits are its millisecond timestamp, so two
accounts created in the same millisecond got the same prefix and the second died
on the unique index. Caught by the second test that created two. Sortable
identifiers are exactly the wrong thing to truncate for uniqueness.

### What the container run caught in Phase 7

The practice has now caught something in every phase it has been applied to,
without exception. This time three, and none was visible to 226 tests.

**Every failure carries a `code` — except the first three an integrator meets.**
`docs/api/` tells a caller to branch on `code` and never on prose, and that was
true of every domain failure and false of a missing credential, a role they do not
hold, and a mistyped path. Those are written by the authorization middleware and
by `UseStatusCodePages`, which know nothing about the domain's `Error` type, and
they were coming back as bare problem documents. A contract that holds everywhere
except at the first failure somebody hits is worse than no contract: it teaches
the right lesson on the happy path and breaks on a Friday. Fixed in one place —
`CustomizeProblemDetails` — because that is the single point every problem
document in the host passes through.

**The console's Save button 500'd, on the Phase 4 EF trap, third instance.**
`RuleSetEditor.Apply` added rules through `ruleSet.Rules`, and EF infers
`Modified` rather than `Added` for a new child reached through a **tracked**
parent's navigation when its key is already set — which every client-generated
UUIDv7 is. It emitted an `UPDATE` against a row that did not exist and died with a
`DbUpdateConcurrencyException` about affecting 0 rows.

What makes it worth recording is **why six phases of tests missed it**. Creating
a draft adds children to a *new* parent, which is fine. Only `ReplaceAsync` has a
tracked one — and every test of `ReplaceAsync` asserted a **refusal**: the wrong
kind, an active set, a rule from another vocabulary. Every refusal returns before
`SaveChangesAsync` is reached. The whole happy path of the editor was untested,
and the only caller that takes it is a person with a mouse: the simulator clones
with its rules inline, which is `CreateDraftAsync`.

> **A test suite that only ever asserts refusals has not tested the method.** That
> is the lesson, and it generalises past this file. `A_draft_can_actually_be_saved`
> now exists, and it was verified by reverting the fix and watching it fail.

**The demo stack would have adopted the development database.** `compose.demo.yaml`
declared no project name, so it inherited `name: countflow` from `compose.yaml` —
and `./scripts/demo.sh` on a machine where somebody had been developing would take
over the dev stack's containers *and its `postgres-data` volume*, seeding demo
users and three admin-role API keys into a database that was never meant to be
public. It now declares `name: countflow-demo` and gets its own volumes.

Worth recording because of **how** it was found: it was the reason the demo stack
had not been run end to end. The gap in the verification and the defect were the
same fact, and writing down "I could not run this because it would collide" is
what made the collision visible as a bug rather than an inconvenience.

**The demo stack has since been run from an empty database**, beside the dev stack,
and the numbers are its own:

```
Countflow simulator → http://api:8080/
authenticated as supervisor (key cfk_uQmiNB…), operator (key cfk_mZ3vch…) and admin (key cfk_Di4rg7…)
promoted 'Simulated cycle count (WH1)' v1 for WH1 with no quiet period
60 receipts, 60 putaways (6 overridden), 12 orders (2 lines short), 26 picks,
9 despatches, 4 count plans, 30 counts (5 with a variance)
```

Note the totals differ from the dev-stack run on the same seed — 26 picks against
28, two lines short against none. That is correct and worth understanding before
somebody reports it: the seed fixes the *decisions*, not the warehouse they are
made in, and this one starts with empty racking rather than a fortnight of prior
stock, so allocation goes short more often. Same seed, same starting state, same
warehouse; same seed, different starting state, different warehouse.

### The influence report, and the finding that was not a flaw

Run over a freshly simulated warehouse. All three kinds replay **100% exactly** —
120/120 putaway, 48/48 allocation, 8/8 cycle count — so the fold is sound and the
readings below mean something.

```
Putaway                          weight   asked  realised   spread
  putaway.consolidate_same_item    0.35   35.0%     43.6%    0.444
  putaway.velocity_slotting        0.25   25.0%     32.3%    0.489
  putaway.travel_distance          0.25   25.0%     16.8%    0.192
  putaway.cube_utilisation         0.15   15.0%      7.3%    0.141

CycleCount
  count.velocity                   0.25   25.0%     52.4%    0.725
  count.stock_at_risk              0.20   20.0%     44.3%    0.733
  count.travel_distance            0.15   15.0%      3.3%    0.070
  count.overdue                    0.40   40.0%      0.0%    0.000   ← never discriminates
```

`count.overdue` is the heaviest rule in the set and realised **nothing**, which is
the exact shape of the five normalisation flaws found so far. It was investigated
rather than assumed either way, and **it is not a sixth**.

Pulling a live trace: 79 holdings considered, 9 admissible, and all nine score
`count.overdue = 1` with the note `rules.count.never_counted`. They have all
genuinely never been counted, because `count.not_recently_counted` is a
*constraint* that had already removed the seventy that had been. The rule is
reporting the truth.

The distinction is worth keeping sharp, because it is the one that separates a
bug from a reading. **The flaw signature is a scale that cannot express a
difference that exists** — spreads of 0.0016, 0.008, 0.019 across candidates that
were genuinely far apart. Here the difference does not exist. A four-day-old
warehouse has no variation in last-counted dates for the policy to find; a year of
history would, and then the 0.40 would do something. `count.travel_distance` on
the same decisions now spans 0.5662 → 0.7915, which is the Phase 7 `RouteScale`
fix holding up against a real candidate field.

### Boundaries held

Nothing gained a project reference it did not have. The two additions are each
inside the module that already owned the data — `ApiKey` in Identity, the change
feed in Inventory — and the feed is a read over rows the ledger already had. The
simulator still references nothing at all, which is what makes it the acceptance
test rather than a demonstration of one.

### Deliberately deferred, and why

- **`DecisionKind.Waving` and `TaskAssignment`.** See above. A partition is not a
  winner, and that is an engine question.
- **Webhooks.** The change feed is a pull. A push needs delivery guarantees, a
  retry policy, endpoint verification and a dead-letter story — every one of which
  is a subsystem, and none of which an integrator needs before they have a working
  poll. The cursor is durable, so adding push later changes nothing about the
  contract.
- **Per-key scopes.** Refused on the merits, and [decision 7](decisions.md#7-the-public-surface-is-the-surface) states the price out
  loud: a role is the smallest unit of authority grantable to an integration, so a
  key that reads stock but cannot post an adjustment is not expressible. The
  mitigation is an account per integration holding the least role that covers it.
- **Key rotation as an operation.** Issue, revoke, and hold both for the overlap —
  which is rotation, done by hand. A `POST /keys/{id}/rotate` that returned a new
  secret and scheduled the old one's death is a nicety, not a capability.
- **Provisioning a host and DNS.** Out of scope for a session with no cloud
  account. What is delivered is a one-command stack and four steps in
  `docs/api/hosting.md`.
- **The handheld's offline command queue.** Still deferred, six phases running.
  Every write still carries a client-generated idempotency key, which remains the
  hard half.

### Proven by test — 22 in `IntegrationSurfaceTests`, plus 1 in `ReplayTests`

- A key reaches the endpoints a person reaches, and `GET /auth/me` says who it is
  and that it is a machine.
- **A key carries its account's roles and nothing more** — an operator-role key is
  refused by the same `RequireRole` that refuses a human operator, and an
  admin-role key is admitted by the same one that admits a human admin. The test
  that exists to show there is no second vocabulary.
- A service account cannot log in with a password; a human cannot be given a key.
- The secret is shown once; the prefix identifies and does not authenticate.
- A revoked key stops working immediately and stays in the listing.
- An expired key is refused.
- **A key cannot mint a key, even holding `Admin`** — a leaked credential must not
  be able to issue itself a permanent replacement.
- **A key may revoke — including its own.** Burning a leak should never wait for
  somebody to log in.
- A bad key does not fall through to a good bearer token.
- The feed hands out every change exactly once, ascending, and walking it in pages
  of two reconstructs the ledger exactly.
- **The feed holds a movement back until no earlier one can still arrive** —
  verified by setting the lag to zero and watching the test fail.
- A credential over its limit is refused with `Retry-After` and a code, and its
  neighbour is unaffected. Health probes are never limited.
- Every refusal carries a `code`, including the ones no handler wrote.
- **The acceptance test: an integration drives both warehouse loops on three
  machine credentials alone**, with the roles split as the command-line simulator
  splits them.
- **A draft can actually be saved** — see above.

---


## Phase 8 — the handheld's offline command queue

Deferred out of five phases running, and by Phase 7 the largest single gap
between what this product claims and what it does. [decision 3](decisions.md#3-two-react-spas-not-blazor-and-not-nextjs) chose a PWA on the
argument that the handheld "must keep working through WiFi dead zones"; for seven
phases the shell was cached and every write went straight to `fetch`, so a device
in a dead aisle started up and could do nothing.

**Done when** an operator can lose the network mid-shift, keep working the jobs
they already hold, and have every command land exactly once when it comes back —
and when the commands the warehouse *refused* in the meantime are in front of
them in a form they can act on. Both hold, driven by hand against the
containerised stack with the API stopped and started under a live device:

```
Offline — work is queued        2 to send
  Saved on this device · Confirmed at WH1-STOR-AMB-A01-02-03-A
  It has not reached the warehouse yet. It goes as soon as there is a signal — carry on.

Needs you
  Cycle count  36 × SKU-10000  counted 34   CNT-2026-0009#1
    The warehouse says   The warehouse could not be reached, after several tries.
    What to do           It is still saved here. Try again when you have a signal…
    [Try again]  [Got it]

#357 Pick             48  A01-01-02-A → PACK-BENCH-00-00-01   confirmed offline, posted once
#358 Pick             55  A01-01-02-B → PACK-BENCH-00-00-01   confirmed offline, posted once
#360 CountAdjustment   2  A01-02-03-A → WH1-VIRT-ADJUSTMENT   stalled, retried, posted once
```

### The hard half was already done, and it was not the queue

The idempotency guarantee that makes a replayed queue safe had been in place
since Phase 2 — see
[decision 8](decisions.md#8-the-handheld-writes-to-a-durable-queue) — which is
why this phase adds **no endpoint, no module and no second write path**.

What nobody had thought about is the part that turned out to be the whole
feature: **what happens to a command the warehouse refuses forty minutes after
the operator made it.** `work.task_already_completed` is not an error. It is news
about a building that moved on while somebody was out of contact, and it leaves
goods in a different state from `inventory.insufficient_stock`. The full argument
is [decision 8](decisions.md#8-the-handheld-writes-to-a-durable-queue); the decisions worth carrying:

- **Every write goes through the queue, online or not.** No offline branch in any
  screen. A queue reached only when `navigator.onLine` is false has never run on
  the happy path.
- **The idempotency key is the record's primary key**, so there is nowhere in the
  restore path where one could be minted.
- **Only assertions are queueable.** "I confirmed this job here" queues; "give me
  the next job" is a server decision and says it needs a signal. The corollary is
  that a claimed task can be worked offline *if the device already has it*.
- **A queued command belongs to one operator.** The ledger records who posted a
  movement, so OP001's putaway waits for OP001 — visibly, including on the
  sign-in screen where that person will be standing.
- **Optimistic state is never dressed as success.** A held confirmation says
  "saved on this device", in warning tone, with the sentence that says it has not
  reached the warehouse.

### What was built

- **`apps/handheld/src/lib/outbox/`** — `Outbox`, the driver: submit, drain
  oldest-first, recover anything caught mid-flight by a power cut, and four
  outcomes whose boundary is the design (`accepted`, `refused`, `unwell`,
  `unreachable`). `store.ts` is IndexedDB directly — Dexie would have cost more
  than the whole feature did — with an in-memory twin the driver's tests run
  against. `dispatch.ts` is the one place a command becomes an HTTP request, and
  it calls the published endpoints and nothing else.
- **The reconciliation screen**, `QueueScreen.tsx`: what you did (from the
  device's own snapshot — if the server refused it there is nothing on the server
  to look it up from), what the warehouse says (grouped by the kind of news, not
  by code), and what to do now. Nothing is dismissed automatically; a *pending*
  command cannot be cleared at all.
- **A cached view of the jobs an operator holds**, reconciled against
  `GET /work/tasks?mine=true&status=Assigned` rather than merged into it.
- **The service worker stopped updating itself** — `registerType: 'prompt'`,
  `injectRegister: null`, and `src/serviceWorker.ts` registering against the
  browser API rather than through `workbox-window`. The update is applied when
  the device is quiet. Not for durability: a reload mid-drain is safe by
  construction. It is that a reload takes away a half-typed count, which is the
  one piece of work on the device with no record anywhere.
- **5.6 kB gzipped, measured.** The handheld went 126.2 → 131.8 kB for the queue,
  and 132.3 kB with the fixes below. **[decision 3](decisions.md#3-two-react-spas-not-blazor-and-not-nextjs)'s "~64 KB gzipped" is stale** and
  now carries its correction rather than a quiet edit.

### Boundaries held

No endpoint was added, no module was added, and Inventory still has exactly one
posting path. A "sync" endpoint taking an array was considered and refused: the
server's idempotency guarantee is per command and so are the refusals, so an
array would have needed a per-element result contract — this queue's problem
restated on the wrong side of the wire — and it would have been a second way to
write a movement, which [decision 7](decisions.md#7-the-public-surface-is-the-surface) spent a phase refusing.

### What the container run caught in Phase 8

The practice has now caught something in every phase it has been applied to. This
time **four**, and two of them were older than this phase.

**A dead API behind a reverse proxy answers 502.** Caddy fronts the API, so
`docker compose stop api` produces a *response* rather than a rejected `fetch`.
Screens that decided "we must be offline" by catching an exception therefore
never decided it — and the session restore, seeing a non-2xx, signed the operator
out and stranded the queued work on a device nobody could send it from.
`unanswered(status)` is exported from `dispatch.ts` for the reads for this reason.
**A mocked `fetch` cannot show you this, because a mock has no proxy in front of
it.**

**The task cache only ever grew.** Entries were written when a task was read and
nothing took them out, so Home advertised two putaways the warehouse had finished
with twenty minutes earlier. On a handheld that is an instruction to move a pallet
somebody has already moved. `reconcileTasks` prunes against the warehouse's list,
keeping anything with a command still unsent — that row is the device's own record
of work in flight and is not the warehouse's to withdraw.

**A stalled command had no way back.** `STALL_AFTER` stops the automatic retry so
a fault cannot hold a shift up — but `drain` walks *pending* commands only, so a
stalled one was unreachable by every automatic path **and by the "send them now"
button**, and the only action on its card was the one that deletes it. Four
minutes of 502s was enough to reach it, and the screen said "it is still saved
here" about work nothing would ever send. `Outbox.retry` gives the operator the
retry the automatic one stopped doing, and the count that stalled during the
walkthrough was sent by pressing it: movement #360, posted once, fifteen minutes
after it was scanned.

**The reachability flag latched.** The badge reports whether the *warehouse*
answered rather than whether the radio is on, which is right — and a drain only
learns anything if it had something of the signed-in operator's to send. A device
with an empty queue, or one holding only the previous operator's stranded work,
stayed "offline" on a perfectly good network with **"next job" disabled and
nothing on the screen able to disagree**. Seen for real when OP002 signed in to a
device holding OP001's unsent work. Three things fix it and all three are needed:
a successful sign-in notes what it just proved, the Home refresh probes instead of
gating on the flag it updates, and `startOutbox` heartbeats while the device
believes itself unreachable.

**And one that was not Phase 8's at all.** `createCountflowClient` refreshes its
token with a hand-written `fetch` — the one call in the package not typed against
the generated schema — and it still pointed at `/api/auth/refresh`. Phase 7 moved
the surface under `/api/v1`, that path became a 404, and the client reads any
non-OK refresh as "the session is gone for good": **every expiring token signed
the user out instead of being refreshed, in both apps, and the refresh-token
rotation built in Phase 0 had never once run from a browser.** On the handheld it
meant a reload after a battery swap ended the shift's session with the operator's
unsent work still on the device — the exact failure [decision 8](decisions.md#8-the-handheld-writes-to-a-durable-queue) says must not
happen. Demonstrated by ageing a stored token and reloading (signed out), then
verified fixed the same way (signed in, refresh token rotated).

The generalisable half: **the OpenAPI drift check only covers what is generated.**
One hand-written path was outside it and stayed wrong for a phase and a half.

### The service worker, and how it was actually verified

Service workers are disabled in the in-app browser — a trivially valid one fails
to register there too — so this was done in a real Chromium (Brave 151) driven
over the DevTools protocol:

- **First load installs and activates**, scope `/handheld/`, 10 precached entries.
- **A cold start with the origin stopped.** `docker compose stop web`, then
  reload: the app renders from the precache while `curl` gets nothing and a page
  `fetch` throws. Emulating offline *in the page* is not enough and is worth
  knowing — a controlling worker fetches from its own target, and the emulation
  does not reach it. **The control:** unregister the worker, clear the caches, and
  the same reload lands on the browser's error page.
- **A new build does not take the page while work is pending.** With one command
  unsent, a rebuilt bundle installed and sat in `waiting`; the page never
  reloaded. The moment the queue reached zero unsent, the update applied and the
  page came back on the new bundle. The old build's own precache is what served
  the page in between.

### Proven by test — 21 in `test/outbox.test.ts`, plus 4 i18n parity

The suite covers the driver: what it sends, in what order, whose work it will not
touch, and what it does with each of the four answers. It does **not** cover
durability, and says so — that is a claim about IndexedDB and a killed process,
and a test asserting it against a memory store would be asserting a fiction.

- A command made with no network is kept; the key it was scanned under is the key
  that is replayed; one caught mid-flight by a power cut is sent again rather than
  abandoned.
- A refusal is kept and put in front of the operator, and does not stop the
  commands behind it. A dead network *does* stop the drain, because the command
  after this one is going nowhere either.
- A server that keeps faulting steps aside — and **a stalled command can still be
  sent by the operator when the warehouse is back**, under the same key, with the
  attempt count reset. Only a stalled one can be: a refusal is an answer.
- Another operator's work is never sent under the signed-in operator; signing out
  does not throw unsent work away; a pending command cannot be cleared and a
  refused one can.
- One intent submitted twice is one command, and the second does not reset the
  first's attempts or its refusal.
- A 429 or a 408 is not an answer about the command, and neither is a proxy's 502.
- The task cache is reconciled against the warehouse rather than merged into.
- The three refusals the phase was written for are three different pieces of news.

### Deliberately deferred, and why

- **Receiving offline answers nothing about where to put it.** The receipt is an
  assertion and queues; the putaway suggestion is a decision over a warehouse the
  device cannot see, and it arrives when the receipt does. A device that guessed
  a location would be inventing a decision trace, which [decision 4](decisions.md#4-every-decision-carries-its-reasoning) forbids.
- **Nothing prunes a device's queue on the server side.** A handheld left in a
  charging rack holding somebody's confirmations holds them until that person
  signs in. That is the honest consequence of the ledger recording who did the
  work, and the alternative — a supervisor sending somebody else's scans — is a
  falsehood in an append-only record.
- **Counts are plural-blind in the catalogue.** "1 scans have not reached the
  warehouse" is the existing convention across every `{{count}}` string in `en.ts`
  and `tr.ts`, not a Phase 8 regression. Fixing it is a plural-rule change to
  `packages/i18n`, which is a small subsystem rather than a wording tweak.
- **The queue holds two command kinds** — a dock receipt and a task confirmation
  — because those are the two writes a handheld makes. Adding a third is a
  `CommandKind` and a branch in `dispatch.ts`.

---

### What the container run has caught, in every phase it has been applied to

Phases 4 and 5 found three scoring flaws by hand — allocation's travel rule and
FEFO, then counting's travel rule — every one of them a preference normalised
against the wrong scale, every one invisible to the test suite and obvious in a
warehouse with stock in it.

**Phase 6 found the fourth, and a machine found it.** The influence report, run
over a simulated warehouse, showed `putaway.travel_distance` weighted 0.25 and
realising 0.8% of the ranking. The route composite `zone × 1,000,000 + position`
was being divided by the building's largest composite, and with zone pick
sequences at 0, 10, 20, 90, 99, 100 that put every bin in the ambient hall within
one part in a hundred thousand of every other. `RouteScale` ranks zones ordinally
instead and gives each an equal slice; travel's realised share went 0.008 → 0.237.

That is the argument for the machinery in one paragraph. The first three took a
person, a container and an afternoon each. The fourth took one run of a test.

**Phase 8 found none, and that is the finding.** The influence report over a
freshly simulated warehouse reproduced 121/121 putaway, 50/50 allocation and
13/13 cycle-count decisions exactly, and the two flags it raised are the two
already understood — `allocation.travel_distance` using the full range, which is
deliberate, and `count.overdue` never discriminating, which is a four-day-old
warehouse having no variation in last-counted dates rather than a scale that
cannot express one. What Phase 8 found instead were four defects in the client,
which is where the phase's new code was. **The instrument points where the work
is.**

**Phase 7 found a fifth, hiding inside the fix for the fourth** — `RouteScale`
divided a position by the largest rather than re-basing to its own minimum, which
is invisible against the whole building and is the original disease against a
handful of candidates. And then it found **two defects that are not scoring flaws
at all**: uncoded transport failures, and an editor whose entire happy path was
untested because every test of it asserted a refusal. Neither was reachable from
the test suite. Both were the first thing that happened when a person used the
product.
