# Reading: pagination and the change feed

## Pagination

List endpoints take `page` (1-based) and `pageSize`, and return one envelope:

```json
{ "items": [ … ], "total": 4471, "page": 1, "pageSize": 50 }
```

`pageSize` is clamped rather than rejected — 1 to 500 on most reads. Ask for a
thousand and you get five hundred and a `pageSize` in the response saying so, so
never assume `items.length` is what you asked for.

Use this for screens and for answering a question about now. Do **not** use it to
synchronise: paging descending through a list that is growing at the front means
rows shift between pages, and a consumer walking it will see some rows twice and
miss others. That is what the change feed is for.

## The change feed

```
GET /inventory/changes?afterSequence=0&limit=200
```

```json
{
  "changes": [
    { "sequence": 4468, "occurredAt": "2026-08-12T09:14:02.881+00:00",
      "sku": "SKU-10001", "lotNumber": "L812-0102", "quantity": 120,
      "fromLocationCode": "WH1-VIRT-SUPPLIER",
      "toLocationCode": "WH1-RECV-DOCK-DOOR-00-00-D01",
      "fromStatus": "Available", "toStatus": "Available",
      "type": "Receipt", "reason": "PurchaseOrder",
      "userName": "wms-bridge", "taskId": null, "decisionTraceId": null,
      "isReversed": false }
  ],
  "nextCursor": 4468,
  "hasMore": true,
  "watermark": 4471
}
```

Every change inventory has ever undergone, in order, exactly once. Send
`nextCursor` back as `afterSequence` and repeat until `hasMore` is false; then
wait and poll again from the same cursor. An empty page leaves `nextCursor`
unchanged, so a caught-up consumer can loop safely.

**Persist the cursor**, not a timestamp. It is a `long` and it is the only piece
of state a consumer needs.

### Why this works without an outbox

`sequence` is a database-assigned total order over the ledger, and the ledger is
append-only — a row is never updated, never deleted, and never re-ordered. So the
sequence *is* the change stream, and there is no event table to keep in step with
the truth, no publisher to fail silently, and no way for the feed to disagree with
what the warehouse believes. It is also a plain read, which is why an integration
consuming it can never become a second way to write a movement.

### The watermark, and why the feed runs slightly behind

`watermark` is the highest sequence the feed will currently hand out, and it lags
the newest entry by about five seconds.

This is the endpoint's correctness rather than a performance compromise. PostgreSQL
assigns a sequence number at `INSERT`, not at `COMMIT`, so two concurrent postings
can take 4470 and 4471 and commit in the other order. A reader served 4471 the
instant it committed would advance its cursor past 4470 and never be shown it —
a consumer's projection diverging silently from a ledger that is itself perfectly
correct. Holding back until every lower sequence has settled removes the hole
entirely.

The practical consequence: a movement you have just posted will not appear in the
feed for a few seconds. That is expected and is not a reason to retry.

### Gaps are normal

The sequence is ascending and complete; it is **not** contiguous. A posting refused
at the unique index on the idempotency key consumes a sequence value on its way
out, so 4469 may simply never exist. Do not treat a gap as a lost message, and do
not try to fetch one.

### Reversals

A correction arrives as its own entry with `reversesMovementId` set, and the entry
it corrects will have had `isReversed: true` from the moment the reversal posted —
including in a page you already consumed. If you keep a local copy of the ledger,
apply the reversal as the new fact it is rather than trying to go back and edit the
original; that is exactly what the ledger itself does.

## Reading the state of things now

The feed gives you the stream. These give you the picture:

| | |
|---|---|
| `GET /inventory/balances` | On hand, committed and available, by item / location / lot / status |
| `GET /inventory/movements` | The same ledger, newest first, filtered by SKU, location, lot, SSCC, type, reason, date |
| `GET /inventory/movements?asOf=…` | The warehouse as it stood at a past instant, summed from the ledger |
| `GET /inventory/accuracy` | Count accuracy by zone and class |
| `GET /inventory/reservations/stranded` | Commitments standing against stock that has since moved |
| `GET /rules/traces/{id}` | Why a directed movement went where it went |

**Available is computed, never stored**: on hand minus open claims. A second
number is a second number to drift, so there is no `available` column anywhere in
this system — which also means you cannot read a stale one.

## Decision traces

Any movement made under direction carries `decisionTraceId`. Fetching it returns
every candidate the rules considered, the constraint that excluded each loser, and
the scored breakdown behind the winner:

```
GET /rules/traces/0198f2c1-…
```

This is the answer to "why did it send the pallet there?", and it is available to
an integration for the same reason it is available to an operator at the rack. A
trace is written in the same transaction as the decision, so a directed movement
without one is not a thing this system can express.
