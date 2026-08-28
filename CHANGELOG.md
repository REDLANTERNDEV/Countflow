# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The compatibility
promise for the HTTP surface is stated more precisely in
[docs/api/versioning.md](docs/api/versioning.md); the reasoning behind the
features is in [docs/history.md](docs/history.md).

## [Unreleased]

### Added

- **Returns, and a fourth vocabulary to decide what becomes of them.** Goods a
  customer sends back are booked into a hold bay, inspected by a person, and then
  routed by rule to one of four outcomes — restock, quarantine, repair or scrap —
  with a decision trace behind every one of them. `POST /outbound/returns` books
  the return and raises the inspections; `POST /outbound/returns/{id}/lines/{lineId}/disposition`
  runs the rules over what the inspector found and carries the answer out.
  `POST /rules/disposition/preview` asks the same question with nothing posted.

  **A return is deliberately two events.** The receipt has existed since Phase 3,
  so the cheap version was one call at the desk that books the goods and decides
  their fate in the same breath. Everything the rules actually score on — sealed
  or opened, repairable or not, what the goods *are* as against what the paperwork
  says — is a judgement somebody makes with the carton in their hands, so a
  disposition taken at the desk would be taken by somebody who has not opened it.
  Goods land at `OnHold` in a hold-role bay, which allocation will not sell out
  of, and an `Inspection` task waits in somebody's queue.

  The rules advise and a supervisor may overrule them, and **the evaluation runs
  and the trace is written either way** — an override that skipped it would
  destroy the only record of what the system would have done. Destroying returned
  stock additionally needs a reason in writing, because every other movement is
  explained by its document and a write-off is explained only by the person who
  ordered it.

  Order lines now carry `returnedQuantity`, summed over what came back rather
  than over what has been decided about it, and separate from what was allocated:
  a returned unit is new supply, not an un-picking. The reasoning is
  [decision 12](docs/decisions.md#12-a-disposition-is-a-verb-and-returns-belong-to-outbound).

- **Two operations the ledger could always perform and no contract offered.**
  `IStockPosting.ChangeConditionAsync` moves a quantity between conditions without
  moving it anywhere — the `StatusChange` movement Phase 1 built the room for and
  nobody had walked through — and `SendForRepairAsync` sends goods to a third
  party against the InTransit counterparty. Neither is a new kind of ledger entry;
  a disposition is a choice *among movements the system already makes*, which is
  why it is a concept beside `StockCondition` rather than a set of new conditions.

- **Passwords can be read back before they are sent.** Both sign-in screens carry
  a reveal control in the field. NIST SP 800-63B asks verifiers to offer one: a
  secret nobody can check pushes people towards shorter passwords and towards the
  clipboard, and it matters most where this app is used — a gloved thumb on a
  handheld in a cold aisle, where a mistyped character is invisible and the only
  feedback is a failed sign-in that looks like a forgotten password.

  Hidden until asked, every time, and the state is never persisted: a device off
  a charging rack must not come back up showing the last operator's password, and
  an aisle is a public place. `PasswordInput` is `type="button"` — inside a form
  that is one attribute away from being the default button, where revealing the
  password would post it — and its target follows the density, so it is 44px in
  the console's sign-in and a full 52px on the handheld.

- **Language and theme, on the handheld.** The console has had both since Phase 3
  and the handheld had neither, which left the two settings that are *most*
  physical — what language the person holding the device reads, and whether the
  screen is legible under a loading bay's floodlight or in a chilled aisle before
  dawn — changeable only from a desk the operator does not sit at. Both sit at
  the foot of the sign-in screen and of Home, sharing the console's storage keys
  and its per-device reasoning: a handheld off a charging rack keeps what the
  shift set on it, rather than inheriting the preference of whoever signs in.

  On the sign-in screen as well as behind it, deliberately. Somebody who cannot
  read the language the device is currently in has to be able to change it
  *before* they can sign in, not after.

### Changed

- **The handheld is sized for a handheld again.** Its controls have always been
  52px, but the type inside them came from `--text-sm` — a step the components
  named directly, so it read 13px in the console's 30px control and only 15px in
  the handheld's 52px one. The height scaled with density and the type did not,
  which is why every handheld screen looked like a desktop form stretched to fit.

  Control text and labels now come from `--control-text`, `--control-text-sm` and
  `--label-text`, which each density points at the step that suits its own
  control height. The console is unchanged to the pixel by construction — its
  roles resolve to exactly the steps its components used to name. On the handheld
  control text goes 15px → 18px, labels 13px → 16px, and the type scale steps up
  with them (headings 30px → 34px), clearing the 16px floor for reading at arm's
  length through safety glasses. Verified with no overflow at 375px and at 320px.

  The console keeps its scale everywhere it is reading tables, and gets a third
  one — `comfortable`, 44px controls with 16px type — on its sign-in screen. That
  screen holds two fields, and a scale sized to fit rows renders it as a fragment
  of an interface rather than the whole of one. It is the only density scoped to
  a container rather than set on `<html>`; the rest of the console measured
  identical after the change.

- **The online marker is gone; only offline is drawn.** The connectivity marker
  was a filled badge in both states, so a normal shift carried a green pill at
  the top of every screen — including sign-in, where it also stretched to the
  full width of the page, being a flex child in a column. `Badge`'s own rule is
  that a marker used for flavour is one operators stop reading as signal, which
  is what an always-on "everything is fine" becomes. Losing the link is news and
  keeps its badge, permanently rather than as a toast, for the reason it always
  had. Working normally is not news, so its absence now *is* the online state and
  the sign-in screen carries nothing. `common.online` is retired from both
  catalogues.

- **Theme and language follow the device until somebody says otherwise.** Both
  defaults were already meant to be "whatever this machine prefers", and one of
  them was not. `i18next-browser-languagedetector` was configured to cache what
  it *detected* into the same slot a deliberate choice is written to, so the
  first launch persisted the raw navigator tag — `en-US` — and every launch
  afterwards read that back as though the operator had picked it. A device later
  switched to Turkish went on showing English forever. Detection no longer writes
  anything; `setLanguage` is the only thing that does, and a stored value that is
  not an exact supported code is cleared at start-up as the artefact it is, so
  the fix reaches installs that have already run.

  The pre-paint theme script in both apps also fell back to a hardcoded theme
  when reading storage threw — light in the two `index.html` copies, dark in the
  `themeBootstrapScript` they have drifted from. It now resolves the system
  preference first and treats that as the floor, so the only thing that overrides
  the OS is an explicit choice. All three copies say the same thing again.

- **The handheld signs in like anything else does.** The operator code sat in a
  `ScanField`, which holds its own value and only hands it over on Enter — so
  keying the code and then the password left "Sign in" greyed out, with nothing
  on screen saying that an Enter nobody had asked for was the missing step. It is
  now an ordinary two-field `<form>`: the code field is autofocused and controlled
  like any other input, Enter submits, and a password manager can fill the pair.

  A form is the better shape for a badge as well, not a concession against one. A
  keyboard-wedge scanner types the code and ends with Enter, and Enter lands on
  the password rather than submitting a half-filled form — so scanning and keying
  arrive in the same place and neither asks the operator to know which one the
  screen was expecting. The submit button is disabled only while the request is
  in flight: implicit submission is skipped when a form's default button is
  disabled, so greying it out until both fields were full would have made Enter
  do nothing at the end of a scan, which is exactly when the password is empty.

- **The console navigates from a side rail, not a top bar.** Fifteen flat
  destinations in one horizontal row had two problems and only one of them was
  mobile. At 375px, fourteen of the fifteen sat outside the viewport with no
  scrollable ancestor — reachable only by panning the whole page sideways, tables
  and all. And at *any* width, fifteen siblings are a lookup rather than a glance;
  the row was already wrapping to two lines at 1280px.

  The rail groups them — master data, operations, insight, setup, with the scan
  test and the tour set apart at the bottom as the old bar's `ml-auto` did — and
  collapses to an icon strip, persisted per device like the theme. Under `lg` it
  becomes a drawer behind a 44px control, so the console stays desktop-first (the
  handheld is still what the floor uses) without being unusable on the tablet a
  supervisor actually checks an order on.

  `apps/console/test/navigation.test.mjs` checks both directions between the
  router and the rail: a page nobody links to and a link that goes nowhere are
  each a failing test rather than something you find later.

### Fixed

- **A customer return credits the customer account it left from.** Booking one
  posted it from the **supplier** counterparty, because the ledger's shape rule
  required every receipt to. The reason code said `CustomerReturn`, so the
  information was recoverable — but the despatch had debited Customer and nothing
  ever credited it back, so the customer contra account only ever grew and the one
  account a distribution centre most wants to reconcile was the one an
  append-only double-entry ledger could not close.

  `MovementType.Receipt` now permits Supplier or Customer, and nothing else. The
  type is the movement's *shape* — goods entering the building from a
  counterparty — and which counterparty is the business fact `MovementReason`
  already carried.

- **The console's sign-in card was rendering at 214px of its intended 360.** The
  cards size themselves with `min(360px, 100%)`, and `place-items-center` on the
  page grid shrink-wraps the row holding them to its own content — so their
  `100%` resolved against a width their content had just decided, and the card
  settled wherever that circle happened to land. The row takes the grid area with
  `w-full` now and `justify-center` still centres the cards, so the percentage
  means the screen again. Verified at 1280px (360px, as intended) and at 375px,
  where it clamps to 339 with no overflow.

- **Every field beside a scan field was impossible to type in.** `ScanField`
  keeps focus for the barcode scanner and took it back from *anything* that
  received it, so focus reached the neighbouring input and was pulled out a tick
  later — the handheld's sign-in password, receiving's quantity and dock, and a
  task's override reason all read as inputs that ignored taps. It now asks where
  focus went: a text field the operator deliberately moved into keeps it, while
  focus that went nowhere, or to a button, still comes back — a scanner left
  pointing at a button would fire it with the Enter that ends every scan.
  `packages/ui/test/focus.test.ts` covers the rule and asserts `ScanField` still
  consults it.

- **Filled buttons were unreadable.** `primary` used `--color-accent-text` — the
  terracotta darkened to be legible *on a surface* — as its label colour *on the
  terracotta fill*: 1.34:1 in light and 1.50:1 in dark, against AA's 4.5:1. The
  console's main action, in both themes. `--color-accent-on` (white, 4.56:1)
  existed for exactly this and had never been referenced. `danger` had the same
  defect one line below and failed in dark only, at 2.88:1, so `--color-danger`
  now has a fill triplet beside it rather than doubling as both a text and a fill
  colour. Dark's `--color-accent-hover` deepens instead of lightening, because
  lightening it left a white label at 3.79:1.

  `packages/ui/test/contrast.test.mjs` now derives every filled variant's
  fill/foreground pair from the component source and fails under 4.5:1 in either
  theme — including a variant that writes its foreground outside the token system,
  which is how the danger button hid.

- **CI could not restore.** `Testcontainers.PostgreSql` 4.13.0 brings in SSH.NET
  2025.1.0, whose `ScpClient.Download` does not validate the paths a remote server
  sends (GHSA-q939-rpr3-3284). NuGet audit runs during restore and
  `TreatWarningsAsErrors` is on, so the advisory failed the backend job. Upstream
  had already moved: 4.14.0 depends on the fixed 2026.0.0.

- **A request body that will not bind is a `400`, not a `500`.** Two defects, one
  symptom. System.Text.Json's defaults respected neither the arity nor the
  nullable annotations of a contract's constructor, so a body that omitted a
  required field — or sent an explicit `null` for one — bound it to `null` and the
  first `Trim()` downstream threw: `POST /auth/login` with `identifier` misspelt
  answered `http.unexpected_error`, as did six other endpoints across five
  modules. And under Development the framework rethrows a binding failure as an
  exception, which the exception handler turned into a `500` regardless — so
  `http.malformed_request`, documented since Phase 7, had never actually been
  emitted by the stack `docker compose up` runs. Both are fixed in the host, so
  every endpoint gets it: the contracts now require exactly the fields the OpenAPI
  document already marked `required`, and Development answers what production
  answers. No published schema changed.

  A *blank* identifier is unaffected and deliberately so — it is a well-formed
  request naming nobody, and still gets the same opaque
  `auth.invalid_credentials` as naming somebody who does not exist.

See [ROADMAP.md](ROADMAP.md) for what is deliberately open.

## [1.0.0] — 2026-08-12

First complete release. The warehouse loop runs end to end, every directed
movement carries the reasoning that produced it, and the surface an integrator
uses is the only surface there is.

### Added

**Inventory — the provable half**

- Append-only double-entry stock ledger. Every change is a movement between two
  locations; nothing is ever updated or deleted and corrections are compensating
  entries. Guarded by init-only properties, a `SaveChangesAsync` override and a
  PostgreSQL trigger that rejects `UPDATE`/`DELETE`/`TRUNCATE`.
- Balance projection written in the same transaction as its movement, with
  point-in-time reconstruction and a reconciler that replays the whole ledger and
  reports drift without ever repairing it.
- Lots, expiry, licence plates (SSCC, check-digit validated, nestable).
- Reservations beside the ledger — a claim is not a movement — with
  available-to-promise computed rather than stored.
- Cycle counts beside the ledger, where a count that agrees with the book posts
  nothing and is still recorded, because accuracy is verified over counted.
- Scrap, adjustments, and reversal as a compensating entry.

**Rules — the explainable half**

- A pure rules engine: ordered constraints, weighted preferences, and a
  `Decision<T>` that *is* the trace — its constructor is internal to the engine,
  so there is no path to a result without its reasoning.
- Three vocabularies on one engine: putaway (7 constraints, 4 preferences),
  allocation (6 and 4, including FEFO and minimum remaining shelf life), and
  cycle count (4 and 4).
- Rule sets as versioned data with `Draft` → `Active` → `Archived` promotion, a
  console editor, and validation performed by building the set through the real
  catalogue at the moment somebody saves it.
- `RuleInfluence` — what a rule was actually worth to a ranking, as opposed to
  what its weight claimed. Four scoring flaws in this project were each a single
  line of that report.

**Shadow replay — the testable half**

- `IStockHistory` folds the ledger forward to hand out the warehouse as it stood
  at any past instant, linear in history rather than quadratic.
- `IShadowReplay` re-runs both rule sets over that reconstruction rather than
  diffing two stored traces, and **measures its own fidelity instead of asserting
  it**: if the rules that made a decision no longer choose what they chose, the
  comparison says so.
- A replay experiment log, and a console screen showing the summary, the
  influence table and the per-decision diff.

**The warehouse loops**

- Inbound: advice notes, the dock receipt that posts the goods, asks the advisor
  and raises the putaway.
- Outbound: sales orders, waves, and release / pick / pack / despatch / cancel,
  with a convergent release that is always safe to run again.
- Counting: count plans raised by policy, on the same work queue as everything
  else.
- One work queue using `FOR UPDATE SKIP LOCKED`, so thirty operators pressing
  "next job" at a shift start each get a different one.

**Master data**

- Location topology (Warehouse → Area → Zone → Aisle → Bay → Level → Position)
  plus virtual ledger counterparties, items with per-packaging-level GTINs, a
  GS1-128 parser, scan resolution, and CSV import with a dry run.
- A 2,245-location / 500-SKU seed with ABC distribution, mixed lot/expiry
  tracking and full packaging hierarchies — rule evaluation only behaves
  interestingly at that scale.

**Integration surface**

- 58 versioned paths under `/api/v1`, and no private sibling: the console, the
  handheld and the simulator are clients of exactly these endpoints.
- Service accounts and API keys, authorised by the same roles a person holds —
  there is no second authorization vocabulary.
- A ledger change feed (`GET /inventory/changes`) — change data capture with no
  outbox, because the ledger was already the change stream — served behind a
  watermark so a concurrent posting can never be skipped.
- Per-credential rate limiting, partitioned on the credential presented rather
  than the identity it resolves to.
- `docs/api/`: authentication, compatibility policy, idempotency, pagination, the
  full error-code table, and both loops worked end to end.

**Clients**

- Console SPA: stock, ledger, inbound, outbound, counting, accuracy, work queue,
  rule sets with preview and drafting, replay, integrations, and a guided tour.
- Handheld PWA: receive, directed putaway and pick, cycle count, "why here?" at
  the rack, and a **durable offline command queue** every write goes through —
  with a reconciliation screen that tells an operator what they did, what the
  warehouse says, and what to do now.
- A warehouse simulator that is a client of the HTTP API and holds no project
  references, so every state it produces is one a real operator could reach.
- en/tr localisation throughout, with `tr.ts` typed against `en.ts` so a missing
  key fails the build. The API returns `{ code, args }` and never prose.

**Operations**

- `docker compose up` for the full seeded stack; `./scripts/demo.sh` for a demo
  whose warehouse is built by a container driving the public API.
- Migrations run in a one-shot migrator container, never on API startup.
- Chiseled API runtime image — no shell, no package manager, non-root — with a
  health check that re-invokes the app binary.
- CI: build with warnings as errors, integration tests against real PostgreSQL,
  an OpenAPI drift check that fails if the published spec and the server disagree,
  and container image builds.

### Fixed

Defects found by driving the running stack rather than by the test suite, kept
here because the class of bug matters more than the individual fix:

- **Five preference-normalisation flaws**, each a scale that could not express a
  difference that existed — travel measured against the wrong field, a route
  composite divided by the building's maximum, a scale not re-based to its own
  minimum. One was found by the influence report rather than by a person.
- **Uncoded transport failures**: a missing credential, a missing role and a
  mistyped path came back as bare problem documents, in a contract that tells
  integrators to branch on `code`.
- **The rule-set editor's Save path**, which had never been executed by a test
  because every test of that method asserted a refusal, and every refusal returns
  before the save.
- **A token refresh calling an unversioned path**, so every expiring session
  signed the user out instead of being refreshed, in both apps.
- **A handheld task cache that only ever grew**, advertising jobs the warehouse
  had finished with twenty minutes earlier.
- **A stalled offline command with no way back** — the automatic retry stopped, as
  designed, and nothing could then send it.
- **A reachability flag that latched**, leaving a device reporting itself offline
  on a working network.

[Unreleased]: https://github.com/REDLANTERNDEV/Countflow/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/REDLANTERNDEV/Countflow/releases/tag/v1.0.0
