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

## Signed webhook delivery

Admin can register `POST /webhooks/subscriptions` with `name`, `url`, and
`afterSequence` (default zero, which replays history). The response shows the
signing `secret` once. List subscriptions at `GET /webhooks/subscriptions`.
Call `POST /webhooks/subscriptions/{id}/verify`: the receiver gets a signed JSON
`webhook.verify` challenge and must return its challenge value exactly as plain
text with HTTP 200. Then `PUT /webhooks/subscriptions/{id}` with `enabled: true`.
The URL is immutable; register a new subscription to change receiver or secret.

A delivery is JSON containing `id`, `type: inventory.changes`, `createdAt`,
`fromCursor`, `nextCursor`, `watermark`, and up to 100 unchanged feed entries.
Headers are `X-Countflow-Delivery`, `X-Countflow-Event`, and
`X-Countflow-Signature`. The signature is base64(HMAC-SHA256(raw UTF-8 body,
UTF-8 signing-secret string)). Validate before parsing, compare digests in
constant time, and durably deduplicate delivery IDs before returning 2xx.
Neither redirects nor non-2xx responses acknowledge delivery.

Jobs probes each enabled subscription once per minute. Eight failures use
exponential backoff from one minute to one hour, then block that subscription as
DeadLetter. `GET /webhooks/subscriptions/{id}/deliveries?page=1` returns the latest
25 attempts/batches per page. `POST /webhooks/subscriptions/{id}/retry` resets
retry eligibility for the pending batch, retaining body/ID. Pausing retains it.
A dead-lettered batch is never silently skipped; the pull cursor remains usable.

`POST /webhooks/triggers` with `name` issues a confidential relative URL once.
POST to it with `Idempotency-Key` (1–128 characters) queues read-only inventory
reconciliation and returns 202 with `requestId`. Repeat that key on retries.
`GET /webhooks/triggers` lists metadata only; `DELETE /webhooks/triggers/{id}`
revokes new calls. Already queued requests finish. Do not put capability URLs in
access logs; configure any upstream proxy similarly to the application's filter.

## Operational dashboard measurements

`GET /work/throughput?warehouseId=&days=7` returns a bounded 1–90-day window,
completion totals by kind and UTC date, and current Ready/Assigned backlog of all
ages. Dates are based on `CompletedAt`, not creation time. Every UTC date appears,
including zero days; today is partial. `from`, `to`, and `observedAt` stamp the read.

`GET /inventory/zone-density?warehouseId=` returns current active bulk/pick-face
location occupancy overall and by zone ID with area/zone codes. Closed parent
areas/zones are excluded. `occupied/locations` is slot occupancy; `cubeUtilization`
is a physical volume ratio available only when all capacities and occupied stock
volumes are known. Null is unknown, and ratios above one are preserved. The read
has its own `observedAt` and does not use the throughput date window.

Both reads require an authenticated caller and an active warehouse. The console
uses the throughput window for the existing `/inventory/accuracy` read, retaining
its counted denominator, and composes these measurements without a report builder.

## Handheld camera scans

The handheld's **Scan barcode** screen captures one barcode with the browser's
native detector, then posts its unchanged `rawValue` to `POST /scan/resolve`.
ASCII 29 separators remain intact: camera input uses the same GS1 parser and
catalogue lookup as a keyboard scanner. The response shows GTIN, SKU, packaging,
base units, lot/expiry/serial/SSCC and coded warnings. This is a read-only
inspection; it does not book a receipt or confirm a task.

Camera access needs a secure context (HTTPS, or localhost during development)
and native DataMatrix support. Formats are queried before requesting permission;
unsupported browsers retain typed/keyboard input. Detection stays on device;
only the decoded string reaches the API. Frame one barcode: several distinct
results never silently select an item. Every camera track stops on capture,
dismissal, backgrounding, navigation or failure, including permission granted
after dismissal. Offline capture retains its string for an explicit online
retry; catalogue resolution itself requires the API.

The native contract is described in the [Shape Detection specification](https://wicg.github.io/shape-detection-api/#barcode-detection-api)
and [browser API documentation](https://developer.mozilla.org/en-US/docs/Web/API/BarcodeDetector).

## FIFO allocation

The allocation preference catalogue includes `allocation.fifo`. Add it to a draft
allocation policy, review its weights/replay, and promote through the existing
rule-set workflow. Existing active policies retain their current rules. FIFO
ranks known receipt ages oldest-first as a normalized weighted preference;
constraints remain mandatory and other preferences may trade off rotation.

Receipt-age notes include `rules.allocation.received_on` with the UTC receipt
instant, or `rules.allocation.receipt_age_unknown`. An age belongs to a holding's
current cohort: internal moves preserve it, mixed known stock keeps the earliest
age until empty, and unknown-origin stock stays unknown. Shadow replay rebuilds
that age at the original checkpoint. This is physical holding rotation, without
accounting layers or valuation.

## Blind bin counts

Supervisors/admins raise a manual audit through `POST /work/bin-counts` with
`LocationCode`, a mandatory reason (up to 120 characters), and `Idempotency-Key`.
Only active physical storage bins are accepted. Work returns a `BinCount` task
with no expected item, lot, pallet or quantity; its source reference records the
requesting supervisor and reason. Ordinary claim/claim-next assigns it and returns
`BinCountVersion` plus `BinCountClaimId`.

Complete through the existing task endpoint with the scanned `ToLocationCode`,
the captured version and claim ID, and `Observed` holding rows. Each row names
`Sku`, nonnegative base-unit `Quantity` (up to four decimal places, below 100
trillion), optional `LotNumber`/`ExpiryDate`/`Sscc`,
and `Condition` (Available by default). Newly found batches/pallets are described
on first sight; item tracking requirements and SSCC checks still apply. Separate
lots, plates and conditions into separate rows; duplicate identities refuse the
request. Send an explicit empty array only after checking the entire bin is empty.
The union of observations and positive book holdings is counted, so unlisted
book holdings become zero. All corrections and observations commit together.

A changed movement version returns `inventory.bin_changed` without recording the
audit. `POST /work/tasks/{id}/bin-count/start` with `Restart: true` refreshes the
baseline and claim ID; discard the old observations and recount. Old claim IDs
after restart or release/reclaim return `work.task_not_yours`. Release and restart
refuse with `work.bin_count_recorded` if Inventory already accepted a confirmation;
retry the saved confirmation to finish closing Work instead. Same key and payload
return the immutable bin receipt after later stock movements; changed payloads
refuse. The result's `BinCount.Counts` carries the original holding findings and
adjustments, including an empty list for a genuinely empty bin.

The handheld caches the assigned task and version. Draft observations are stored
per operator/task/claim; submitted observations use the existing durable outbox.
Queued confirmation is visibly pending until the server acknowledges it. Claims,
restart and fresh barcode resolution require a connection; manually entered
holding identities and confirmations can be captured offline against cached work.
