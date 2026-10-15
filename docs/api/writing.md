# Writing: idempotency

**Every command that changes stock requires an `Idempotency-Key` header.** Not as
a recommendation — a request without one is refused before anything happens:

```json
{ "status": 400, "code": "inventory.idempotency_key_required",
  "detail": "Every write command must carry an idempotency key." }
```

## Why it is mandatory rather than optional

The stock ledger is append-only. Nothing is ever updated or deleted, so there is
no state a duplicate write could be reconciled against afterwards — a second
receipt of the same pallet is simply a second pallet, for ever, and it looks
exactly like a real one. The first anybody hears of it is an argument with a
supplier about a delivery that was never short.

The clients that write most are handhelds on warehouse wireless, and they retry
constantly, because a request that times out over a bad link may well have
committed. An optional guarantee is one that some client somewhere is not using.

## What a key is

Any string up to 128 characters, unique to the *command* rather than to the
attempt. A client-generated UUIDv7 is the obvious choice.

```bash
KEY=$(uuidgen)

curl -s -X POST "$COUNTFLOW/inventory/receipts" \
  -H "X-Api-Key: $APIKEY" \
  -H "Idempotency-Key: $KEY" \
  -H 'Content-Type: application/json' \
  -d '{"sku":"SKU-10001","quantity":120,"toLocationCode":"WH1-RECV-DOCK-DOOR-00-00-D01"}'
```

Generate the key **before** the first attempt and reuse it for every retry of that
same command. A key generated inside the retry loop is not an idempotency key; it
is a fresh command each time, and it will post a fresh movement each time.

## What happens on a retry

Sending the same key with the same command returns the **original result**, with
`wasAlreadyPosted: true`:

```json
{ "movementId": "0198f2…", "sequence": 4471, "quantity": 120,
  "toQuantityAfter": 120, "wasAlreadyPosted": true }
```

Sending the same key with a *different* command is refused with
`inventory.idempotency_key_reused` (`409`). That is the case worth being glad
about: it means a bug that recycles keys is reported rather than silently
replaying the wrong answer.

## Derived keys

Orchestration writes several movements from one command, and each needs its own
key. The convention is derivation: a wave's key begets an order's, which begets a
line's, which begets one per holding the line draws on. Deriving rather than
generating is what makes the whole cascade replayable — running a release twice
under one key produces exactly the same set of movements, not a second set.

If you build keys this way, note that reservations and allocations carry 256
characters where a movement's key gets 128, because the chain gets long. Truncating
to fit would break the very thing the chain exists for.

## Convergent operations

Some writes are safe to repeat for a different reason: they are convergent rather
than transactional.

**Releasing an order** reserves stock, records the allocation, then raises the
picks — three modules, three schemas, no transaction spanning them. A crash
between any two leaves work half done, and running the release again finishes it:
lines already committed are skipped, and the picks are driven off the recorded
allocations rather than off whatever this pass created. Releasing an
already-released order is also how you chase a shortfall — lines that went short
are re-evaluated against a warehouse that has since taken a delivery.

**Raising a count plan** behaves the same way.

Neither excuses you from sending a key. Convergence handles a crash; the key
handles a retry.

## Which writes need one

Every `POST` that ends in a ledger entry or a reservation:

`/inventory/receipts` · `/inventory/transfers` · `/inventory/adjustments` ·
`/inventory/status-changes` · `/inventory/scrap` ·
`/inventory/movements/{id}/reversal` · `/inbound/receipts` ·
`/outbound/orders/{id}/release` · `/outbound/orders/{id}/despatch` ·
`/outbound/waves/{id}/release` · `/work/tasks/{id}/complete` ·
`/counting/plans`

Creating an ASN, an order or a rule set does not, because those carry their own
unique business reference and a duplicate collides on it —
`inbound.asn_reference_taken`, `outbound.order_reference_taken`.

## Corrections

There is no `DELETE` and no edit. A movement posted in error is corrected by a
**compensating entry** — `POST /inventory/movements/{id}/reversal` — which posts a
new movement pointing back at the one it undoes. The original stays exactly as it
was posted.

A movement may be reversed once; a second attempt is `inventory.already_reversed`,
and a reversal cannot itself be reversed. The ledger read marks a reversed entry
`isReversed: true`, because a corrected movement that looks identical to a live one
is how a reader draws the wrong conclusion from a correct ledger.

## Pick-face replenishment

`PUT /replenishment/pick-faces/{locationCode}` saves an opt-in policy for an active,
replenishable Forward location. Supervisor or Admin is required. Supply `sku`,
`minimum`, `maximum`, and `enabled`; quantities use base units, are positive with
maximum greater than minimum, have at most four decimal places, and maximum is
at most 1,000,000,000. Known volume, weight, and temperature constraints apply.
Changing the item requires an empty face with no pending or incoming work.

`GET /replenishment/pick-faces` lists policies with optional `warehouse`, `page`,
and `pageSize` (25 by default, capped at 100). It returns on-hand, available,
incoming, shortfall, last plan/decision trace, and recovery status. Available is
shown for operations; classic min/max evaluates on-hand plus incoming. Policies
are disabled unless explicitly enabled. Jobs probes enabled or pending faces
once per minute, subject to its global runner settings.

`GET /replenishment/plans/{id}` exposes source lines and task states. Supervisor
or Admin can `POST /replenishment/plans/{id}/cancel`; repeat cancellation is safe,
but assigned tasks must first be handed back. Cancellation converges unresolved
keyed tasks, withdraws work atomically, and then releases open source reservations.

Operators use ordinary Work claim, release, and completion endpoints. A
Replenishment task requires its suggested destination, including offline queued
confirmation. Reuse the completion idempotency key on retry. Confirmed Work
completion follows Inventory's reserved Replenishment posting; a queued handheld
confirmation is not yet server acceptance. Disabling the policy does not cancel
its existing tasks.
