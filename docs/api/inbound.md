# The inbound loop, worked

A delivery arrives, is booked in, and ends up in the racking with a written
explanation of why it went where it did.

```
POST /inbound/asns              the supplier says what is coming
POST /inbound/receipts          the truck arrives; goods are booked and a putaway is raised
POST /work/tasks/claim-next     an operator takes the job
GET  /rules/traces/{id}         "why here?"
POST /work/tasks/{id}/complete  the pallet lands and the ledger closes
```

Roles: `Supervisor` for the first two, `Operator` for the rest.

An SSCC receipt registers a durable handoff before posting stock. Until its task
source is acknowledged, the job reports `pendingReceiptCount > 0` and cannot be
claimed or completed. Retry an interrupted receipt with its original key and
body. Hand an assigned pallet task back before receiving more onto that pallet;
otherwise receiving returns `work.pallet_in_use` before adding stock. See
[the receipt invariant](../invariants.md#the-rules-engine) and
[decision 27](../decisions.md#27-a-receipt-reserves-its-handoff-before-it-posts-stock).

## 1 · Tell the system what is coming

An advice note is optional. Skipping it gives a **blind receipt**, which is a real
and common event rather than an error — but with one, every receipt is checked
against what the supplier said.

```bash
curl -s -X POST "$COUNTFLOW/inbound/asns" \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{
    "reference": "ASN-2026-0481",
    "warehouseCode": "WH1",
    "supplierName": "Northgate Foods",
    "expectedAt": "2026-08-13T07:30:00+00:00",
    "lines": [
      { "sku": "SKU-10001", "expectedQuantity": 120,
        "expectedLotNumber": "L812-0102", "expectedExpiryDate": "2027-02-07" }
    ]
  }'
```

No idempotency key: `reference` is the business identity, and sending it twice
collides with `inbound.asn_reference_taken` (`409`).

## 2 · Book it off the truck

```bash
curl -s -X POST "$COUNTFLOW/inbound/receipts" \
  -H "X-Api-Key: $KEY" -H "Idempotency-Key: $(uuidgen)" \
  -H 'Content-Type: application/json' \
  -d '{
    "sku": "SKU-10001",
    "quantity": 120,
    "toLocationCode": "WH1-RECV-DOCK-DOOR-00-00-D01",
    "asnReference": "ASN-2026-0481",
    "asnLineNumber": 1,
    "lotNumber": "L812-0102",
    "expiryDate": "2027-02-07",
    "sscc": "305123450000000018"
  }'
```

```json
{
  "receipt": { "movementId": "0198f2…", "sequence": 4468, "quantity": 120,
               "wasAlreadyPosted": false },
  "sku": "SKU-10001",
  "toLocationCode": "WH1-RECV-DOCK-DOOR-00-00-D01",
  "lotNumber": "L812-0102",
  "sscc": "305123450000000018",
  "workTaskId": "0198f2c9-…",
  "suggestedLocationCode": "WH1-STOR-AMB-A01-01-02-A",
  "decisionTraceId": "0198f2c9-…",
  "consideredCount": 2240,
  "admissibleCount": 1920,
  "asnLine": { "reference": "ASN-2026-0481", "lineNumber": 1,
               "expectedQuantity": 120, "receivedQuantity": 120,
               "outstandingQuantity": 0, "asnStatus": "Closed" }
}
```

One call did four things: posted the receipt to the ledger, asked the rules where
the pallet should go, wrote the trace, and put the putaway in somebody's queue.

Three details that matter:

- **The goods came *from* the supplier counterparty**, not from nowhere. Every
  ledger entry names both sides, so the sum of every quantity ever posted is
  exactly zero. Read the movement back and `fromLocationCode` is
  `WH1-VIRT-SUPPLIER`.
- **`sscc` is what makes this a pallet.** Supplying one means the putaway moves
  the whole licence plate in a single transaction, however many SKUs end up on it.
  Omit it and the putaway is a line at a time.
- **`outstandingQuantity` may be negative**, which is an over-delivery. It is
  recorded, not refused.

Goods cannot be booked straight into racking — a receipt must land in a receiving
location, or you get `inbound.not_a_receiving_location`. The putaway is the second
movement, and it is somebody's job.

### Two refusals worth knowing about

`inventory.lot_required` — the item is lot-tracked and you sent no `lotNumber`.
`inventory.lot_not_permitted` — the item is **not** lot-tracked and you sent one.
Roughly two ambient items in seven carry batches, so an integration that attaches
a lot to everything loses the other five silently until it reads its own error
responses.

`inventory.lot_expiry_conflict` — a lot number *is* a batch, so two deliveries of
`L812-0102` claiming different expiry dates is a conflict, and correctly so.

## 3 · Somebody does the job

```bash
curl -s -X POST "$COUNTFLOW/work/tasks/claim-next" \
  -H "X-Api-Key: $OPERATOR_KEY" -H 'Content-Type: application/json' \
  -d '{"warehouseCode":"WH1"}'
```

Thirty operators pressing this within the same few seconds at a shift start each
get a different job; the queue claims under `FOR UPDATE SKIP LOCKED`, so there is
no retry loop to write. `work.no_work_available` (`404`) is the empty queue.

```json
{
  "task": {
    "id": "0198f2c9-…", "kind": "Putaway", "status": "Claimed",
    "sku": "SKU-10001", "quantity": 120, "lotNumber": "L812-0102",
    "sscc": "305123450000000018",
    "fromLocationCode": "WH1-RECV-DOCK-DOOR-00-00-D01",
    "suggestedLocationCode": "WH1-STOR-AMB-A01-01-02-A",
    "decisionTraceId": "0198f2c9-…"
  },
  "decision": { "…": "the trace, inline" }
}
```

The trace comes back **with** the task rather than needing a second fetch. That is
for the handheld's sake — the wireless in the racking is the least reliable link in
the building, and the second round trip is the one most likely to fail — but an
integration gets it for free.

## 4 · Why there?

```
GET /rules/traces/0198f2c9-…
```

```
Putaway for SKU-10001 ×120 → WH1-STOR-AMB-A01-01-02-A   "Standard putaway" v1
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

A trace is written in the same transaction as the decision, so a directed movement
without one is not a state this system can reach. The excluded sample is one
candidate per distinct reason rather than the first thirty — candidates arrive in
warehouse order, and the first thirty rejections would be thirty dock doors failing
the same constraint.

## 5 · Confirm where it actually went

```bash
curl -s -X POST "$COUNTFLOW/work/tasks/$TASK_ID/complete" \
  -H "X-Api-Key: $OPERATOR_KEY" -H "Idempotency-Key: $(uuidgen)" \
  -H 'Content-Type: application/json' \
  -d '{"toLocationCode":"WH1-STOR-AMB-A01-01-02-A"}'
```

```json
{
  "task": { "status": "Completed", "wasOverridden": false,
            "completedAtLocationCode": "WH1-STOR-AMB-A01-01-02-A" },
  "movements": [ { "movementId": "…", "sequence": 4469, "quantity": 120 } ]
}
```

**Confirming somewhere else is not an error.** Send a different
`toLocationCode` and the movement posts there, `wasOverridden` becomes true, and
the movement still carries the trace that was offered. Overrides are the signal
that a rule set needs tuning, and a system that refused them would simply not be
told. `overrideReason` is worth sending when you have one.

`movements` is a list because a whole-pallet putaway carrying three SKUs posts
three entries — one idempotency key family, one transaction. A pallet that is half
moved is worse than one that is not moved at all.

Retrying with the same key returns the same result. Whole-pallet Inventory
commands also retain terminal refusals: correcting the physical contents does not
make an earlier refused key eligible to move stock. Refresh/re-slot as needed and
submit a new confirmation key. A retry with a changed task, actor identity,
explanation, destination, notes or expected holdings is refused as key reuse.
Confirming completed work under a *new* key returns `work.task_already_completed`.
Composite keys must fit the ledger's 128-character per-movement limit (root at
most 124 characters, with additional room needed above 1000 holdings); notes are
limited to 1000 characters. Handheld keys are UUIDs.

## Receipt advice follows the task

`decisionTraceId`, `consideredCount` and `admissibleCount` describe the effective
putaway task after the receipt is acknowledged. When receipts merge onto a pallet,
these fields preserve the task's existing explanation rather than reporting the
incoming line's separate evaluation. Retrying an old receipt returns the current
task revision and its advice; it does not post stock again.

The additive fields `putawayRevision` (zero before an explicit re-slot) and
`putawayAdviceStale` identify that snapshot. A receipt growing a revised pallet
returns `putawayAdviceStale: true` and `suggestedLocationCode: null`. Its trace
remains available as evidence, but the handheld asks for a supervisor re-slot
before work proceeds. Stale advice is distinct from an evaluation with no
admissible location. Always open the task for current state before acting.

## Re-slot a pallet before taking the job

An Admin or Supervisor can `POST /work/tasks/{id}/reslot` with an
`Idempotency-Key` and `{ "reason": "Delivery contents changed" }`. The task must
be Ready, have a pallet with contents at its origin, and have no pending receipt.
Hand assigned work back first. Evaluation considers every item and lot, saves
immutable revision history and makes the new trace the task's current advice.
Task quantity is refreshed from those contents; receipt source history is retained.

The response contains `current` task detail, `appliedRevision` (including its
frozen contents and previous/new trace IDs), and `wasAlreadyApplied`. Reusing the
same task/key/reason returns the original revision alongside current task state;
a changed reason with that key is refused. GET task detail includes
`putawayHistory`. The console exposes both original and revised explanations.

For a revised task, completion must include `expectedDecisionTraceId` with the
trace shown to the operator. An old or absent value returns
`work.putaway_decision_changed` before movement. Handheld retains that ID in its
offline command. A later acknowledged receipt sets `putawayAdviceStale`; claim
and completion are blocked until another re-slot. Inventory also compares the
revision's saved item/lot/condition/quantity contents under its writer lock before
posting. A difference, including one introduced outside Inbound, returns
`inventory.pallet_contents_changed` without moving stock; request a new re-slot.
Whole-pallet completion saves its request before calling Inventory. While
`completionPending` is true, resume the original device command with the same
`Idempotency-Key` and body. Claims, handback, cancellation, new receipts and new
re-slots return `work.completion_pending`; acknowledged receipt and re-slot
replays remain available. Receipt replies also expose `completionPending` and
suppress the destination until settlement. A changed body under the same task/key
returns `work.completion_key_conflict`; another key cannot displace pending work.
Exceptions keep recovery pending. A definite refusal releases the fence and
retains the error; correcting it requires a new key (and a fresh re-slot for
changed contents). A successful retry returns the completed task without another
stock batch. Keys support up to 124 characters and override reasons up to 500.
Legacy Inventory key-ownership rejection discards the provisional Work attempt
so the correct original request can still recover.

## Where the goods are now

```
GET /inventory/balances?sku=SKU-10001&location=WH1-STOR-AMB-A01-01-02-A
GET /inventory/movements?sku=SKU-10001
```

```
#4468  Receipt  120  WH1-VIRT-SUPPLIER            → WH1-RECV-DOCK-DOOR-00-00-D01
#4469  Putaway  120  WH1-RECV-DOCK-DOOR-00-00-D01 → WH1-STOR-AMB-A01-01-02-A   trace
```

Two entries, both sides named on each, and the second one able to say what
directed it and why.
