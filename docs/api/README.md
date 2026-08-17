# Integrating with Countflow

This is the whole surface. There is no private API behind it — the console a
supervisor uses and the handheld on the floor are clients of exactly these
endpoints, and so is the warehouse simulator in this repository, which holds no
project references and reaches the system only through HTTP. That is a deliberate
choice with consequences, recorded in [decision 7](../decisions.md#7-the-public-surface-is-the-surface).

| | |
|---|---|
| **Base path** | `/api/v1` — every path below is relative to it |
| **Machine-readable** | `GET /openapi/v1.json`, and [`frontend/openapi.json`](../../frontend/openapi.json) in this repository |
| **Media type** | `application/json`; failures are `application/problem+json` |
| **Errors** | [error-codes.md](error-codes.md) — branch on `code`, never on prose |

Read next:

- **[authentication.md](authentication.md)** — bearer tokens, API keys, and why
  there are no scopes.
- **[versioning.md](versioning.md)** — what may change under you and what may not.
- **[writing.md](writing.md)** — idempotency keys, which every write requires.
- **[reading.md](reading.md)** — pagination, and the change feed for keeping an
  external system in step.
- **[inbound.md](inbound.md)** — a delivery from advice note to putaway, worked.
- **[outbound.md](outbound.md)** — an order from release to despatch, worked.
- **[hosting.md](hosting.md)** — running the demo stack and pointing it at a domain.

## The shortest possible start

```bash
export COUNTFLOW=https://countflow.example.com/api/v1
export KEY=cfk_…                     # from an administrator; see authentication.md

curl -s -H "X-Api-Key: $KEY" "$COUNTFLOW/auth/me"
```

```json
{
  "id": "0198f2c1-…",
  "userName": "wms-bridge",
  "displayName": "ERP bridge",
  "operatorCode": "SVC-4A9C1E0B2D77",
  "roles": ["Supervisor"],
  "isServiceAccount": true
}
```

That response is the answer to both halves of "did my credential work": who the
API thinks you are, and what it will let you do. `roles` is the entire
permission model — there is no scope list to cross-reference.

## What the system is, in four sentences

Stock truth is an **append-only double-entry ledger**. Nothing is ever updated or
deleted; corrections are compensating entries, balances are a projection of the
ledger rather than the source of truth, and the stock at any past instant is a sum
rather than something remembered.

Work the system directs — where a pallet goes, which stock fills an order, which
bay gets counted — is decided by **rule sets held as data**, and no decision can
exist without the trace behind it: every candidate considered, the constraint that
excluded each loser, and the scored breakdown behind the winner.

Those two together make the third thing possible: a candidate rule set can be
**replayed against real history**, because the ledger can rebuild the warehouse as
it stood at any past decision and the engine can be handed those inputs again.

The practical consequence for an integrator is that this API has **one way to
change stock and several ways to read it**, and that asymmetry is on purpose.

## The shape of every request

**Reads** are `GET`, take query parameters, and are safe to repeat.

**Writes** are `POST` and every one of them requires an `Idempotency-Key` header.
This is not advisory. A write without one is refused with
`*.idempotency_key_required` before anything happens. Handhelds on a warehouse
wireless link retry constantly, and against an append-only ledger a retry that
posts twice is a correctness bug rather than a cosmetic one. See
[writing.md](writing.md).

**Failures** are RFC 9457 problem documents with a `code` extension:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Conflict",
  "status": 409,
  "detail": "There is not enough unreserved stock in that holding.",
  "code": "inventory.insufficient_available_stock",
  "traceId": "00-0e18d05030a2cc3a840ff58fb7332b00-d16c43c5aae769c5-01"
}
```

`code` is stable and is what you branch on. `detail` is English prose for a log
line and may be reworded at any time — the console never displays it, translating
`code` instead. `traceId` is the W3C trace context of that request and is what to
quote when asking about one.

## Rate limits

Requests are counted per credential, in a sliding window. Over the limit is `429`
with a `Retry-After` header in seconds and the code `rate_limit.exceeded`. The
limit is per *credential*, so one noisy integration cannot exhaust another's
budget — or a person's. Defaults are 600 requests a minute for a credential and 60
for an unauthenticated caller; a deployment may set its own, and the demo runs
tighter than both.

## Conventions worth knowing before you start

- **Quantities are decimal, in the item's base unit.** Packaging levels are
  resolved at the edge — `POST /scan/resolve` turns a scanned case barcode into a
  SKU and a base-unit quantity — and nothing downstream reasons about cases or
  pallets.
- **Enumerations travel as names**, not ordinals: `"type": "Putaway"`. Inserting a
  member renumbers ordinals and silently breaks stored values; names do not.
- **Identifiers are never formatted.** GTINs, SSCCs, SKUs and location codes are
  opaque strings — no grouping, no locale. Quantities, weights and dates are
  values and are formatted for display.
- **Timestamps are ISO 8601 with an offset**, always UTC on the wire.
- **A GTIN identifies a packaging level, not an item.** One SKU carries a
  different GTIN on each of Each, Inner, Case and Pallet.
- **Zone codes are unique within an area, not globally.** Every distribution
  centre has a `DOCK` zone in both receiving and shipping.
- **An expiry date belongs to a batch**, so expiry tracking implies lot tracking,
  and two deliveries claiming different expiry dates for one lot number is a `409`.
