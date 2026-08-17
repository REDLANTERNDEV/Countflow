# Countflow documentation

The [README](../README.md) covers what Countflow is and how to run it. This is
for people changing it or building against it.

## Changing the code

These two first, in this order:

| | |
|---|---|
| **[architecture.md](architecture.md)** | The tour: repository layout, what each module owns, how a request travels, and what enforces the boundaries. |
| **[invariants.md](invariants.md)** | The rules that must survive your change, each with the failure it prevents. Also the per-subsystem notes — read the section for the corner you are working in. |

Then, as needed:

| | |
|---|---|
| **[gotchas.md](gotchas.md)** | Things that behave differently from how they read, in EF Core, PostgreSQL, Docker, Vite and the browser. Symptom first. |
| **[adr/](decisions.md)** | Why the architecture is shaped this way, one decision per file, each with what it cost. |
| **[history.md](history.md)** | How it was built, phase by phase — and what driving the real stack found that the tests could not. |
| **[../CONTRIBUTING.md](../CONTRIBUTING.md)** | Setup, the commands, the review bar, and how we verify. |

## If you are integrating with Countflow

**[api/](api/)** is the whole surface — there is no private sibling to it. It
covers authentication, the compatibility policy, idempotency, pagination and the
change feed, the error-code table, and both warehouse loops worked end to end
with real requests.

Start at [api/README.md](api/README.md); the shortest useful path is
authentication → writing (idempotency) → the loop you care about.
