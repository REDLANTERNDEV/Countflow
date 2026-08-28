# Countflow — notes for Claude

A WMS for a single-company distribution centre. This file is the operational
shorthand for an agent working in this repository; **the substance lives in
`docs/`**, and it lives there once so it cannot drift.

## Read before changing anything

| | |
|---|---|
| [docs/invariants.md](docs/invariants.md) | **Not negotiable.** The rules, and the per-subsystem notes — read the section for the corner you are in. |
| [docs/gotchas.md](docs/gotchas.md) | Things that behave differently from how they read. Check here before debugging anything surprising. |
| [docs/architecture.md](docs/architecture.md) | What each module owns, how a request travels, what enforces the boundaries. |
| [docs/decisions.md](docs/decisions.md) | Why the architecture is this shape, and what each decision cost. |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Setup, the full command list, the review bar, and how this project verifies things. |
| [ROADMAP.md](ROADMAP.md) | What is deliberately open, and the argument for each. |
| [docs/history.md](docs/history.md) | How it was built, and what every container walkthrough caught. |

## Commands

```bash
docker compose up                  # full stack, seeded
cd backend && dotnet build         # TreatWarningsAsErrors is on
cd backend && dotnet test          # Testcontainers — needs Docker
cd frontend && pnpm dev            # HMR against the containerised API
cd frontend && pnpm --recursive typecheck
cd frontend && pnpm test           # i18n parity + the handheld's outbox driver
cd frontend && pnpm export:openapi && pnpm generate:api   # after any endpoint change
```

The simulator, the demo stack and the EF migration incantation are in
[CONTRIBUTING.md](CONTRIBUTING.md#the-commands). Credentials come from the
environment, never the command line.

## Working agreements

The verification discipline — run it rather than trust the suite, verify a guard
by breaking what it guards — is
[CONTRIBUTING.md § How we verify](CONTRIBUTING.md#how-we-verify). Follow it; it
is not repeated here. What is specific to working in this repository as an agent:

- **The user commits.** Stage the work and say so; do not run `git commit`.
- **Finish the docs with the code.** A new rule goes in `docs/invariants.md`, a
  lost afternoon in `docs/gotchas.md`, a changed decision in `docs/decisions.md`, a
  user-visible change in `CHANGELOG.md`. **Never add a second copy of something
  already in `docs/` — link to it.** More documents is not more documentation.
- A global pre-commit hook blocks `password\s*=\s*"…"`. Test credentials go
  through a `Credentials(identifier, secret)` helper, never inline literals.
- Committing to `main` warns but does not block. That is expected.

## Current state

All nine phases of the original plan are complete — see
[CHANGELOG.md](CHANGELOG.md) for what that means feature by feature, and
[ROADMAP.md](ROADMAP.md) for what was deliberately left out. Last full
verification: 247 backend tests, 36 frontend tests, both apps typecheck, 62
versioned paths with no OpenAPI drift, and the containerised stack driven by hand
with the API stopped and restarted under a live handheld.

Returns and the fourth rule vocabulary landed after that plan and are the first
test of [decision 11](docs/decisions.md#11-a-wave-is-a-sequence-of-decisions-not-a-partition)'s
claim that a new vocabulary needs no change to the kernel; the receipt is
[decision 12](docs/decisions.md#12-a-disposition-is-a-verb-and-returns-belong-to-outbound).
