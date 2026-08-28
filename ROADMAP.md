# Roadmap

Not unfinished work — things deliberately left out, each with the reason, so that
picking one up starts from the argument rather than from scratch.

## Open, ranked

### 1. The waving vocabulary

The design question this item opened with — whether a wave needs a new kernel
shape — is settled and no longer open. It does not:
[decision 11](docs/decisions.md#11-a-wave-is-a-sequence-of-decisions-not-a-partition)
admits waving as a fourth vocabulary on the unchanged engine, and states what
that costs.

What is left is the build, and it is ordinary vocabulary work: a `WavingContext`
over the wave being filled, constraints and preferences written about a
`SalesOrder`, a catalogue, an advisor that fills to a cap and seeds the next
wave, a fourth rebuild in `ShadowReplay`, and the seeding trap that decision names
in advance. `DecisionKind.TaskAssignment`, batch picking and cluster picking are
the same shape and come after it.

**Sizeable, and now unblocked.**

### 2. Re-slotting a mixed pallet

A trailer unloaded SKU by SKU onto one pallet is one putaway job, and the
suggestion is made when the first SKU lands. Re-deciding as the pallet grows
means a decision context over a pallet's *whole contents* — a fourth decision
context — and it would rewrite an explanation an operator may already have been
shown.

Deferred out of three phases as "belongs to the next one", then explicitly
declared standalone work rather than a detour off something else.

### 3. Webhooks over the change feed

`GET /inventory/changes` is a durable cursor and integrators can poll it today. A
push needs delivery guarantees, a retry policy, endpoint verification and a
dead-letter story — every one of them a subsystem. Purely additive whenever
somebody actually wants it; the contract does not change.

### 4. Plural rules in `packages/i18n`

Every `{{count}}` string in both catalogues reads "1 scans", "1 need you",
"1 days". It is a rule per locale and a typed helper, and it is the most visible
remaining roughness in text an operator reads every shift. **The best first
contribution on this list.**

### 5. Nothing repairs a stranded commitment

An ad-hoc transfer out of reserved stock leaves the claim standing against a bin
that no longer holds the goods. The failure is loud and correct — the alternative
is silently re-allocating somebody's order — and a supervisor screen surfaces
them, but the repair is still "cancel the order and release it again". A targeted
re-home needs allocation to run for a single line.

### 6. The blind bin count

A count is of a *holding*, not of a bay, so it cannot discover an item the system
has no record of there at all. `CountAsync` can already express "twelve cases in
a bay we think is empty"; what is missing is a task shape that asks the question.

### 7. Counting has no schedule

A plan is raised by a supervisor pressing a button. A nightly job is a cron entry
away and was deliberately left out: a counting policy should be watched by a
person for a week before it runs unattended.

## Out of scope

Countflow is a WMS for a single-company distribution centre. What that rules out
— labour management, slotting optimisation, 3PL billing, robotics, yard
scheduling, inventory valuation, multi-tenancy — is listed on the
[front page](README.md#scope), and it is a scope decision rather than a backlog.
Valuation is argued at length in
[decision 10](docs/decisions.md#10-the-warehouse-owns-quantity-the-erp-owns-value),
because it is the one on this list that gets more expensive to reverse the longer
the ledger runs.

## How to pick something up

Open an issue saying which item and how you plan to approach it, especially for
2 — it changes shared machinery, and the design conversation is cheaper before
the code exists. That conversation has already happened for 1, and its outcome is
[decision 11](docs/decisions.md#11-a-wave-is-a-sequence-of-decisions-not-a-partition).
[CONTRIBUTING.md](CONTRIBUTING.md) has the rest.
