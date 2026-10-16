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

Implemented: supervisors explicitly re-slot Ready pallets against their whole
contents. Immutable revisions preserve the original explanation and the reason
for each replacement. Later receipts require a fresh revision; handheld commands
retain the trace they acknowledged, and Inventory checks the recorded load under
its physical writer lock. Durable completion intents survive process and session
loss without repeating stock movements. See [the operation](docs/api/inbound.md)
and [decision 30](docs/decisions.md#30-pallet-confirmation-survives-the-work-lock-session).

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

### 5. Repairing a stranded commitment

Implemented: a supervisor can repair one released order line without cancelling
its order. Only stranded, unclaimed picks are withdrawn; replacement supply is
chosen by the existing allocation rules. Original allocations and decision traces
remain in history, retries resume a durable repair, and unavailable replacement
stock remains an explicit shortfall. See [the operation](docs/api/outbound.md#repair-a-stranded-commitment)
and [decision 25](docs/decisions.md#25-a-repair-replaces-a-promise-and-its-writes-keep-the-lock-session).

The pick still fails loudly if its promised stock has moved. Repair is a separate
supervisor decision, never a silent reallocation under an operator.

### 6. The blind bin count

A count is of a *holding*, not of a bay, so it cannot discover an item the system
has no record of there at all. `CountAsync` can already express "twelve cases in
a bay we think is empty"; what is missing is a task shape that asks the question.

### 7. Counting has no schedule

Cycle counting now supports opt-in per-warehouse schedules through the supervisor
editor and shared job runner ([operation](docs/api/hosting.md#opt-in-cycle-counting)).
Automatic counting remains off by default: a supervisor should watch the policy
for a week before enabling it.

### 8. A label has no way to reach a printer

`POST /labels/prints` returns ZPL and stops there, which was a deliberate cut
([decision 13](docs/decisions.md#13-a-label-kind-is-code-a-label-template-is-data)):
generation is fully verifiable by asserting on the markup, and transport is a
separate problem with four candidate answers that do not resemble each other.

A **direct socket to port 9100** is trivial and puts the API on the warehouse LAN,
which is a networking commitment rather than a feature. A **local print agent** is
the answer most WMS vendors reach and costs an installer, an update path and a
support surface on every site. **WebUSB** puts the printer next to the person who
pressed the button and works only in Chromium, over HTTPS, with the device
physically attached. **Cloud print** outsources all of it and adds a dependency
that decides whether pallets can be labelled.

The shape of the cut is what makes this cheap to pick up: nothing above the
generator knows a printer exists, so whichever lands consumes the `zpl` field and
changes no code in `Modules/Labels`. Which one is right is a question about a
particular building, and there is no default worth guessing.

### 9. Only one label kind exists

`LabelKind` has a single member. Location labels for rack faces and carton labels
for pick-and-pack are both obvious next kinds, and each is a binding contract plus
a binder — the deliberate cost of
[decision 13](docs/decisions.md#13-a-label-kind-is-code-a-label-template-is-data)
being paid a second time. That the pallet kind dropped in without touching the
renderer, the vocabulary seam or the template lifecycle is untested until a second
one does; one kind proves nothing, which is the same thing
[decision 4](docs/decisions.md#4-every-decision-carries-its-reasoning) says about
one rule set.

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
