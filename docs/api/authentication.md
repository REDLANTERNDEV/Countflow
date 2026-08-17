# Authentication

Two credentials, one set of permissions.

| | People | Integrations |
|---|---|---|
| Presents | `Authorization: Bearer <jwt>` | `X-Api-Key: cfk_…` |
| Obtained by | `POST /auth/login` | an administrator issues it |
| Lifetime | an hour, rotated by `POST /auth/refresh` | until revoked or expired |
| Permissions | its user's roles | its account's roles — **the same roles** |

Which handler runs is chosen by the presence of `X-Api-Key`. Send both and the key
is what is checked; a bad key is a `401` and does **not** fall back to the token.
That is deliberate — a revoked integration that kept working for anyone holding an
open session would not really be revoked.

## There are no scopes

An integration that may release a wave holds `Supervisor`. One that may promote a
rule set holds `Admin`. The check that admits it is character for character the
check that admits a person doing the same thing.

This is the single most consequential decision on this page, so it is worth being
explicit about what was rejected. A scope vocabulary beside the role vocabulary is
two sets of rules answering one question — "may this caller do this?" — and the
second set is the one nobody updates. The first endpoint added after the scopes
were designed gets a role requirement and no scope requirement, or the reverse,
and the gap is silent until somebody finds it. One vocabulary cannot drift out of
step with itself.

The price is real and worth stating: you cannot mint a key that reads stock but
cannot post an adjustment, because `Supervisor` can do both. What you can do —
and should — is give each integration its own account with the least role that
covers its job, so a bridge that only reads is an `Operator` and never sees an
adjustment endpoint at all.

| Role | Covers |
|---|---|
| `Operator` | Claims and completes work, reads stock, resolves scans. The largest group. |
| `Supervisor` | Everything above, plus inbound and outbound orchestration, adjustments, count plans, drafting rule sets and running replays. |
| `Admin` | Everything above, plus master data, credentials, and promoting a rule set to the floor. |

## Signing in as a person

```bash
curl -s -X POST "$COUNTFLOW/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"identifier":"supervisor","password":"…"}'
```

`identifier` accepts a username, an email, or an **operator code** — the short
string on a badge, because typing an email address on a scanner keypad wearing
gloves is miserable.

```json
{
  "accessToken": "eyJhbGciOi…",
  "accessTokenExpiresAt": "2026-08-12T13:02:44.118+00:00",
  "refreshToken": "wR2b…",
  "user": { "id": "…", "roles": ["Supervisor"], "isServiceAccount": false }
}
```

Refresh tokens **rotate**: each `POST /auth/refresh` returns a new one and retires
the one you sent. Presenting a retired token looks like theft, so every active
session for that user is revoked and the caller gets a `401`. If you hold a
refresh token in more than one process, you will trip this. Don't.

An unknown user and a wrong password return the identical problem document, so the
endpoint cannot be used to find out which accounts exist.

## Issuing a machine credential

Administrators only, and **only from a signed-in session** — an API key cannot
mint an API key, even one holding `Admin`. The role check passes; what refuses it
is one floor up. A leaked key that could issue itself a permanent replacement
would survive the revocation of the key that leaked, and cutting an integration
off has to actually cut it off.

```bash
# 1. An account for the integration. Give it the least role that covers the job.
curl -s -X POST "$COUNTFLOW/auth/service-accounts" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"wms-bridge","displayName":"ERP bridge","roles":["Supervisor"]}'

# 2. A key for it.
curl -s -X POST "$COUNTFLOW/auth/service-accounts/$ID/keys" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"nightly stock sync","expiresInDays":365}'
```

```json
{
  "key": { "id": "…", "prefix": "cfk_7Qa2Xv", "createdAt": "…", "expiresAt": "…" },
  "secret": "cfk_7Qa2Xv3nR8tK…"
}
```

**`secret` appears exactly once.** There is no endpoint that reads it back, and
that is not a feature waiting to be added — only a SHA-256 of it is stored, so
recovering it is not possible. Lose it and you revoke the key and issue another.

`prefix` is the leading, non-secret portion. It identifies a key in a list or a
log line and authenticates nothing; presenting it as a credential gets a `401`.

## Revoking

```bash
curl -s -X POST "$COUNTFLOW/auth/service-accounts/keys/$KEY_ID/revoke" \
  -H "Authorization: Bearer $TOKEN"
```

Effective on the next request. Unlike issuing, revoking is reachable **with** an
API key, including its own — an integration that notices its credential has leaked
should be able to burn it at three in the morning without waiting for somebody to
log in.

Keys are never deleted, only revoked, and a revoked key stays in the listing. A
movement posted by an integration in March names the account that made it, and a
credential row that vanished would make that log line unattributable.

## Two invariants that will surprise you if you don't know them

**A service account cannot log in with a password.** It is created without a
password hash at all, and `POST /auth/login` refuses it explicitly on top of that.

**A person cannot hold an API key.** Issuing one against an ordinary user account
is refused with `identity.not_a_service_account` rather than a vague `404` — the
account exists; it is simply the wrong kind of principal.

A credential belongs to exactly one kind of caller. That is what makes "revoke
every machine credential" and "make everyone change their password" two separate,
complete operations.

## Storing the key

Send it in `X-Api-Key` on every request; there is no handshake and nothing shorter-lived
to exchange it for. Treat it as you would a database password — environment
variable or secret manager, never a command-line argument (which is visible in the
process table), never a query string (which is logged by every proxy on the way),
never a source file.
