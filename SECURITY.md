# Security policy

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting: go to the
[Security tab](https://github.com/REDLANTERNDEV/Countflow/security) of this
repository and choose **Report a vulnerability**. That opens a private thread
visible only to the maintainers.

Useful things to include, as far as you have them:

- what an attacker gains — read another tenant's stock, post a movement they
  should not be able to post, escalate a role, exhaust the service;
- the affected version or commit;
- a request sequence or proof of concept;
- whether it needs an authenticated credential, and which role.

You will get an acknowledgement within a few days. Please give a reasonable
window for a fix before disclosing publicly; a fix and an advisory will be
published together, and you will be credited unless you would rather not be.

## Scope

In scope: anything in this repository — the API, both frontends, the container
images and the compose files.

Particularly interesting, because of what this system is:

- **Authorization holes.** Roles are the entire permission model. An API key
  carries its account's roles and is judged by the same `RequireRole` a person
  is, so a way to reach an endpoint without the role it declares is a real
  finding.
- **Anything that writes to the stock ledger outside `StockLedger`.** The ledger
  is append-only and one posting path is the whole guarantee.
- **Idempotency-key handling.** A key that can be made to replay somebody else's
  command, or that fails to prevent a double post, is a correctness *and* a
  security issue.
- **Credential handling** — API keys are hashed with a single SHA-256 on purpose
  (the secret is 256 bits of CSPRNG output, and a work factor on an
  unauthenticated path is a denial-of-service vector). Passwords go through
  ASP.NET Identity's hasher. If you can show that reasoning is wrong, that is a
  finding.

Out of scope: the demo stack's deliberately weak, documented credentials
(`Countflow#2026` and friends — they exist only when demo seeding is switched
on); findings that require an already-compromised host or database; missing
hardening headers with no demonstrated impact; and automated scanner output with
no exploit path.

## Deployment notes that are your responsibility

Countflow ships secure defaults for a laptop, not for the internet. Before
exposing an instance:

- set `POSTGRES_PASSWORD`, `JWT_SIGNING_KEY` (32+ bytes) and `SEQ_ADMIN_PASSWORD`
  — the production compose file requires them;
- leave `SEED_DEMO_USERS` and `SEED_DEMO_DATA` false, which is the default;
- give each integration **one service account holding the least role that covers
  its job** — a role is the smallest unit of authority a key can hold;
- put it behind TLS. `CADDY_SITE_ADDRESS` with a real hostname provisions a
  certificate automatically.

[docs/api/hosting.md](docs/api/hosting.md) has the full walkthrough.

## Supported versions

The project is pre-1.0 in release terms: fixes land on `main`, and that is the
supported version.
