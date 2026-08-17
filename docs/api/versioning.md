# Versioning and compatibility

The version is `v1` and it lives in the path: `/api/v1/…`. There is no
unversioned alias and no header-based negotiation. `/api/inventory/balances`
returns `404`, deliberately — a caller who guessed is told immediately rather
than being served something that might move under them later.

It is a version of the **whole surface**, not of an endpoint. Per-endpoint or
date-stamped versions exist to let a large API move in pieces; this one is
fifty-odd paths, and a single number a reader can hold in their head is worth
more than the granularity. What actually protects you is the policy below.

## What will not change within `v1`

- A path will not be removed, and its method will not change.
- A required request field will not be added.
- A field will not be removed from a response, nor have its type changed.
- A field's meaning will not change. If the answer to a question changes, the
  question gets a new field; the old one keeps answering the old question or is
  deprecated and left in place.
- An error `code` will not be reused for a different condition, and the status
  class it maps to will not change.
- An enumeration member will not be removed or renamed.

## What may change without notice

- **New paths.** Whole capabilities may appear.
- **New optional request fields**, and new optional query parameters. A field you
  do not send behaves exactly as it did before it existed.
- **New response fields.** Parse permissively: unknown fields must be ignored, not
  rejected. This is the single most common way an integration breaks on a change
  that is compatible by every reasonable definition.
- **New enumeration members.** A new `MovementType` or `WorkTaskKind` can appear
  the day a capability ships. Handle an unrecognised member as "something I do not
  model" rather than as an error — a `switch` with no default is a time bomb here.
- **New error codes**, including on endpoints you already call. Branch on the
  codes you handle and treat the rest as generic failures of their HTTP status.
- **Prose.** Every `detail` and `title` is English for a human reading a log, and
  is reworded freely. Never match on it.
- **Ordering** where it is not documented. Where order is part of the contract —
  the ledger's sequence, a decision trace's ranking — it is stated and stable.
- **Rate limits**, which are a deployment's decision rather than the API's.

## Deprecation

A field or endpoint that is going away is marked `deprecated` in the OpenAPI
document first and keeps working. Nothing published in `v1` is removed inside
`v1`; removal is what a `v2` is for.

## How a `v2` would arrive

`v2` would be mounted alongside `v1` at `/api/v2`, not in place of it, and `v1`
would keep serving until it was deliberately retired with notice. The surface is
mounted in one place in the host — `MapCountflowModules` hands every module a
route group already under the prefix, and modules map *relative* paths — so
standing up a second version is a change to the host rather than an edit to fifty
files. That is not an implementation detail; it is the reason the promise above is
one this project can keep rather than one it would rather not test.

## Pinning

Pin the version you chose, in one place, as a constant of your own:

```csharp
private const string Api = "/api/v1";
```

That is literally what the warehouse simulator in this repository does. Deriving
the prefix from something the server tells you at runtime would mean a deployment
that stood up `v2` could move your client onto it without anybody deciding to,
which is the one thing versioning exists to prevent.

## Reading the contract

`GET /openapi/v1.json` serves the OpenAPI 3.1 document from any deployment, and
[`frontend/openapi.json`](../../frontend/openapi.json) is the same document
checked into this repository. It is generated from the running endpoints rather
than maintained beside them, and CI regenerates it and fails the build on any
drift — so it cannot quietly describe an API that no longer exists. The
TypeScript client both front ends use is generated from it, which means the
console is broken by an undocumented change before you are.
