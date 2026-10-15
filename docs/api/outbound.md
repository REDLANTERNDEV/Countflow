# The outbound loop, worked

An order becomes a reservation, a pick list, a packed consignment and a despatch,
and the ledger sums to zero from Supplier to Customer across the whole round trip.

```
POST /outbound/orders               what the customer wants
POST /outbound/orders/{id}/release  the rules choose the stock and raise the picks
POST /work/tasks/claim-next         a picker takes one
POST /work/tasks/{id}/complete      the stock moves to the bench
POST /outbound/orders/{id}/pack     nothing outstanding
POST /outbound/orders/{id}/despatch it leaves the building
```

Roles: `Supervisor` for the order lifecycle, `Operator` for the picks.

## 1 · The order

```bash
curl -s -X POST "$COUNTFLOW/outbound/orders" \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{
    "warehouseCode": "WH1",
    "reference": "SO-2026-1041",
    "customerName": "Harborview Retail",
    "stagingLocationCode": "WH1-PACK-PACK-BENCH-00-00-01",
    "shipBy": "2026-08-15",
    "lines": [ { "sku": "SKU-10001", "quantity": 120 } ]
  }'
```

`reference` is the identity; a duplicate is `outbound.order_reference_taken`.

A line may name a batch — `"lotNumber": "L812-0102"` — and if it does, that is
absolute: the allocation constraint will refuse every other batch rather than
treat it as a preference.

## 2 · Release it

```bash
curl -s -X POST "$COUNTFLOW/outbound/orders/$ORDER_ID/release" \
  -H "X-Api-Key: $KEY" -H "Idempotency-Key: $(uuidgen)" \
  -H 'Content-Type: application/json' -d '{}'
```

```json
{
  "order": {
    "status": "Released",
    "lines": [{
      "lineNumber": 1, "sku": "SKU-10001",
      "orderedQuantity": 120, "allocatedQuantity": 120, "outstandingQuantity": 0,
      "allocations": [{
        "id": "0198f3…", "quantity": 120,
        "locationCode": "WH1-STOR-AMB-A01-01-02-A",
        "lotNumber": "L812-0102", "expiryDate": "2027-02-07",
        "decisionTraceId": "0198f3…", "workTaskId": "0198f3…",
        "taskState": "Open", "pickedQuantity": 0
      }]
    }]
  },
  "allocationsRaised": 1,
  "tasksRaised": 1,
  "shortfallQuantity": 0
}
```

Release did three things: reserved the stock, recorded the allocation, and raised
the picks.

**Nothing moved.** A reservation is a claim on stock, not a movement of it — the
pallet has not gone anywhere and a stocktake would find no trace of it — so it is
not a ledger entry. Available-to-promise is computed as on hand minus open claims
and never stored, so you cannot read a stale one.

### Shortfall is not failure

`shortfallQuantity` is what the rules could not find stock for. A line half
allocated is half picked today and the rest chased tomorrow, which is what
warehouses actually do. `outstandingQuantity` on the line says how much is still
uncommitted.

To chase it, **release the order again**. Lines already fully committed are
skipped and the rest are re-evaluated against a warehouse that has since taken a
delivery. This is the intended operation, not a workaround.

### Release is convergent

It touches three schemas and no transaction spans them, so it is built to be
re-runnable rather than atomic. A crash after reserving leaves a hold that the
next release finds and reuses before allocating; a crash after recording leaves a
commitment with no job, and the next release raises it, because the picks are
driven off the recorded allocations rather than off whatever the current pass
created. Running release again is always safe and always moves the order closer
to fully released.

### Why that stock?

Every allocation carries a `decisionTraceId`:

```
Pick 120 × SKU-10001 from WH1-STOR-AMB-A01-01-02-A → WH1-PACK-PACK-BENCH-00-00-01
  "Standard allocation" v1 · SO-2026-1041#1
    +0.450  expires 2027-02-07, in 179 days
    +0.200  this would clear the last 120 here
    +0.062  in a reserve location
    +0.100  zone AMB, 0% along the route
```

One decision can produce several allocations: the rules rank the holdings and the
fill walks that ranking taking what each has, so a line filled from two bays is one
decision and one trace, and no judgement escapes it.

## 3 · Pick it

A pick is claimed and completed through exactly the endpoints a putaway is —
`claim-next`, then `complete`. The one thing that differs is what the confirmation
means downstream, and that is the system's business rather than yours.

```bash
curl -s -X POST "$COUNTFLOW/work/tasks/$TASK_ID/complete" \
  -H "X-Api-Key: $OPERATOR_KEY" -H "Idempotency-Key: $(uuidgen)" \
  -H 'Content-Type: application/json' \
  -d '{"toLocationCode":"WH1-PACK-PACK-BENCH-00-00-01"}'
```

Under the covers a pick names **the reservation it discharges** rather than an
item, a quantity and a location. Picking stock nobody promised, picking more than
was promised, and picking from a holding the rules did not choose are therefore
not merely checked for — they are unexpressible. The claim is consumed inside the
same transaction as the movement, so two confirmations of one pick cannot both
succeed however they race.

A pick also breaks into the pallet: the stock comes off the licence plate, and a
whole-pallet pick empties it. That is the honest description of what happened at
the bench.

## 4 · Pack

```bash
curl -s -X POST "$COUNTFLOW/outbound/orders/$ORDER_ID/pack" \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{}'
```

Refused with `outbound.picks_outstanding` while any pick is still open. Packing is
the assertion that everything that is going to be picked has been.

## 5 · Despatch

```bash
curl -s -X POST "$COUNTFLOW/outbound/orders/$ORDER_ID/despatch" \
  -H "X-Api-Key: $KEY" -H "Idempotency-Key: $(uuidgen)" \
  -H 'Content-Type: application/json' -d '{}'
```

```json
{
  "order": { "status": "Despatched", "despatchedAt": "2026-08-13T11:41:08+00:00" },
  "movements": [ { "movementId": "…", "sequence": 4474, "quantity": 120 } ]
}
```

**The trailer is built from what was picked** — never from what was ordered, and
not even from what was allocated. A line the rules could not fill and a pick nobody
confirmed are both absent from it. A despatch built from intentions is how a
customer is invoiced for stock still sitting in the racking.

Every line goes in one transaction, because a shipment that is half despatched is
worse than one that is not despatched at all, and only the second is obvious.

Despatching before packing is `outbound.order_not_despatchable`; despatching an
order nobody picked is `outbound.nothing_picked`.

## Cancelling

```bash
curl -s -X POST "$COUNTFLOW/outbound/orders/$ORDER_ID/cancel" \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"reason":"Customer withdrew the order"}'
```

Releases the holds and withdraws the picks nobody has started. If an operator is
holding one, it is `outbound.picks_in_progress` — a job cannot be cancelled out
from under somebody standing at the bay.

## Waves

`POST /outbound/waves` groups orders; `POST /outbound/waves/{id}/release` releases
every order in it through the identical path, and reports one combined shortfall.
A wave is a grouping a person makes; nothing here decides one by rule.

## The whole round trip

```
GET /inventory/movements?sku=SKU-10001
```

```
#4468 Receipt   120  WH1-VIRT-SUPPLIER            → WH1-RECV-DOCK-DOOR-00-00-D01
#4469 Putaway   120  WH1-RECV-DOCK-DOOR-00-00-D01 → WH1-STOR-AMB-A01-01-02-A   trace
#4472 Pick      120  WH1-STOR-AMB-A01-01-02-A     → WH1-PACK-PACK-BENCH-00-00-01 trace
#4474 Despatch  120  WH1-PACK-PACK-BENCH-00-00-01 → WH1-VIRT-CUSTOMER
                                              sum over every holding: 0
```

Four entries, both sides named on each, two of them able to say what directed them
and why, and nothing created or destroyed anywhere along the way.


## Pick tours

`POST /work/tours/claim-next` claims ready picks and returns their walking order.
Send `Idempotency-Key` (at most 128 characters); reuse it after a lost response.
The optional body accepts `warehouseId` and `maxStops` (default 12, clamped to
1–50). The server seeds from the most urgent oldest pick and takes nearby work
in the same warehouse and priority band. An empty or contested queue returns
`work.no_work_available` (404). Reusing an operator's key returns that tour's
current state, even after its work has progressed.

`GET /work/tours?mine=true&open=true&page=1&pageSize=20` lists tours;
`GET /work/tours/{id}` returns one. Responses include ordered `stops`, original
`stopCount`, current `remaining`, `releasedStops`, zone strategy segments, and
travel estimates. Estimates are proxy steps; null means unmeasurable, and
`stepsSaved` can be negative. Costs cover travel between the first and last
stops, following the applied strategy; approach and final exit are excluded.
Fallback/support reasons are stable `route.*` codes for client localization.
Tasks also expose `pickTourId` and `tourSequence`.

Complete each stop through the existing `POST /work/tasks/{id}/complete`,
including its `pickTourId` in the body. A completion from a released or
reassigned tour is refused, so cached work cannot outlive its claim.
`POST /work/tours/{id}/release`, with its own retry key, returns unfinished stops
of the caller's tour to the queue. Completed stops stay on the tour. Retrying
that release key succeeds; a fresh release with nothing left returns 409.

`GET /zones?warehouse=WH1` lists zone routing and geometry support.
Supervisors/admins use `PUT /zones/{id}/pick-route` with
`{ "strategy": "route.s_shape", "hasRearCrossAisle": true }` to update both
settings together. Available codes are `route.sequence_ordinal`, `route.s_shape`,
`route.return`, and `route.midpoint`. S-Shape/Midpoint require a rear cross-aisle;
all geometric strategies require aisle and bay data. Existing zones default to
sequence order with no assumed cross-aisle.
