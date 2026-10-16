# Contributing to Countflow

This code decides where real pallets go and what a company believes it owns, so
**correctness and explainability outrank velocity**: a change that makes the
system unable to prove what happened will not be merged however elegant it is.
The rules themselves are in [docs/invariants.md](docs/invariants.md).

---

## Getting set up

The only hard prerequisite is **Docker**. For working on the code you also want
**.NET 10 SDK**, **Node 24+** and **pnpm 11**.

```bash
git clone https://github.com/REDLANTERNDEV/Countflow.git
cd Countflow
docker compose up
```

That brings up PostgreSQL, runs every module's migrations in a one-shot migrator
container, seeds a 2,245-location / 500-SKU warehouse, and serves the console at
<http://localhost:8000>, the handheld at <http://localhost:8000/handheld/> and
the API at <http://localhost:8080>. Sign in with `operator` / `Countflow#2026`
(also `supervisor` and `admin`; demo seeding is off unless `Seed:DemoUsers` is
set, which the development compose file does for you).

## The commands

```bash
# Backend
cd backend && dotnet build            # TreatWarningsAsErrors is on
cd backend && dotnet test             # Testcontainers — needs Docker running

# Frontend
cd frontend && pnpm install
cd frontend && pnpm dev               # HMR against the containerised API on :8080
cd frontend && pnpm --recursive typecheck
cd frontend && pnpm test              # i18n parity + the handheld's outbox driver
cd frontend && pnpm build
```

**After adding or changing any endpoint**, regenerate the typed client or CI will
fail:

```bash
cd frontend && pnpm export:openapi && pnpm generate:api
```

**A new EF migration** — the migrator is the startup project, always:

```bash
cd backend && dotnet dotnet-ef migrations add <Name> \
  --project src/Modules/Countflow.Modules.<Module> \
  --startup-project src/Countflow.Migrator \
  --context <Module>DbContext \
  --output-dir Persistence/Migrations
```

**A realistic warehouse to look at**, built by a client driving the public API
rather than by a seeder:

```bash
export COUNTFLOW_SUPERVISOR_SECRET=… COUNTFLOW_OPERATOR_SECRET=… COUNTFLOW_ADMIN_SECRET=…
cd backend && dotnet run --project src/Countflow.Simulator -- --warehouse WH1 --days 4
```

Credentials come from the environment, never from the command line — an argument
is visible in the process table. An API key wins over a password:
`COUNTFLOW_<ROLE>_API_KEY` first, then `COUNTFLOW_<ROLE>_SECRET`, for
`SUPERVISOR`, `OPERATOR` and `ADMIN`. The same `--seed` builds the same warehouse
every time, deliberately: a guard that finds a flaw one run in five is a guard
people switch off.

If `dotnet test` hangs with no container in `docker ps`, it is a Docker
credential helper, not the tests — see
[docs/gotchas.md](docs/gotchas.md).

---

## How we verify

**A green suite is necessary and not sufficient.** Every phase of this project
from the fourth onward shipped at least one defect that 200+ passing tests could
not see and that appeared within minutes of a person using the running system.
Four scoring flaws, a 500 on a screen's Save button, a token refresh pointed at a
path that no longer existed. The pattern is consistent: **tests confirm the model
you already have; using the product is what corrects it.**

So, in order of preference:

1. **Run it.** `docker compose up`, then drive the thing you changed through the
   console, the handheld or `curl`. Behind Caddy, with the real database, at seed
   scale. A mock has no reverse proxy in front of it and cannot show you that a
   dead API answers `502` rather than failing to answer.
2. **Verify a guard by breaking what it guards.** Revert your fix and watch the
   new test fail. A regression test that passes with and without the fix is not a
   test, and this project has shipped two of them.
3. **Write the test at the level the bug lives.** The stock ledger is tested
   against real PostgreSQL through Testcontainers, because an in-memory provider
   does not have `FOR UPDATE SKIP LOCKED`, `NULLS NOT DISTINCT`, or the trigger
   that refuses an `UPDATE`. Durability of the handheld's queue is *not* unit
   tested, because asserting it against an in-memory store would be asserting a
   fiction.
4. **Beware a suite that only asserts refusals.** Six phases of tests missed a
   500 in the rule-set editor because every test of that method asserted a
   refusal, and every refusal returns before the save.

Useful shorthand for a change that touches decisions: run the simulator, then a
replay run, and read the **influence report**. It says what each rule was
actually worth to a ranking, as opposed to what its weight claimed. Four of this
project's scoring bugs are a single line of that report.

---

## Making a change

1. **Open an issue first** for anything that adds a concept, an endpoint or a
   dependency. Endpoints especially: there is no internal API here, so anything
   you map is a public contract we have promised not to break within v1
   ([decision 7](docs/decisions.md#7-the-public-surface-is-the-surface)).
2. **Branch from `main`.** Small, focused commits; present-tense subject lines
   that say what changed and, where it is not obvious, why.
3. **Match the surrounding code.** Vertical slices (`Features/<UseCase>/` holds
   the endpoint, command, handler and validator together), `Result`/`Error` for
   domain failures rather than exceptions, package versions in
   `Directory.Packages.props` or the pnpm catalog and never inline.
4. **Localise your strings.** The API returns `{ code, args }` and never prose. A
   new coded error needs an entry in `packages/i18n/src/locales/en.ts` *and*
   `tr.ts` (which is typed against `en.ts`, so a missing key fails the build), and
   a row in [docs/api/error-codes.md](docs/api/error-codes.md). One a handheld can
   meet also needs a line in `refusals.ts` saying what the operator should do.
5. **Update the docs that your change makes wrong.** A note in `docs/decisions.md` if you changed a
   decision; [docs/invariants.md](docs/invariants.md) if you added a rule;
   [docs/gotchas.md](docs/gotchas.md) if you lost an afternoon to something — that
   file is the most valuable one in the repository and it is written by whoever
   suffered.
6. **Run the full gate before pushing** (below).

### The gate

CI runs four jobs and so should you:

| | |
|---|---|
| **Backend** | `dotnet build -c Release` with warnings as errors, then `dotnet test` — 226 tests, real PostgreSQL. NuGet audit runs during restore, so a newly disclosed advisory fails the build. |
| **Frontend** | `pnpm typecheck`, `pnpm test`, `pnpm build`; from the repository root, `node --test scripts/test/*.test.cjs` verifies demo isolation with synthetic settings and Docker Compose. |
| **API contract** | Exports the OpenAPI document from the running host, regenerates the client, and **fails on any diff**. This is what makes the published spec impossible to drift. |
| **Container images** | Builds the api, migrator and frontend images. |

### Pull requests

Describe **what changed, why, and how you verified it** — "ran the counting loop
through the handheld against the compose stack" is worth more than a list of
files. Link the issue. Screenshots for anything an operator or supervisor sees.

Reviews are direct and about the code. Expect questions about failure modes,
idempotency and what happens on a retry; they are the house style, not a
criticism of you.

---

## Good first contributions

- **Wording.** The handheld's refusal advice is a real feature — `queue.advice.*`
  in `packages/i18n`. If any of it reads like an error message rather than an
  instruction, fixing that is a genuine improvement.
- **A third locale.** `tr.ts` is typed against `en.ts`, so the compiler tells you
  exactly what is missing. No backend change is involved.
- **Documentation gaps**, particularly anywhere `docs/api/` assumes knowledge you
  did not have.
- **The open items in [ROADMAP.md](ROADMAP.md)**, which say what is missing and
  why it was left.

Please avoid, unless an issue has agreed it first: new endpoints, new
dependencies, reformatting sweeps, and anything that adds a second way to write
to the stock ledger.

## Licence

By contributing you agree that your contributions are licensed under the
[GPL-3.0](LICENSE), the same licence as the project.
