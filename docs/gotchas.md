# Gotchas

Things that behave differently from how they read, in .NET, EF Core, PostgreSQL,
Docker, Vite and the browser. Symptom first, because that is what you will have
when you come looking. Phase numbers refer to [history.md](history.md).

**If you lose an afternoon to something that is not here, add it.**

---

- **A namespace that shares a name with a type shadows it inside that namespace.**
  The disposition vocabulary lives in `Countflow.Modules.Rules.Dispositions`,
  plural, and the plural is load-bearing. Under a `…Rules.Disposition` namespace
  every mention of the `Disposition` enum inside it resolves to the *namespace*,
  so `Disposition.Restock` does not compile and the fix that suggests itself —
  fully qualifying it as `SharedKernel.Contracts.Disposition.Restock` in every
  rule — is noise on every line of five files. Name the folder for the collection,
  not for the type.
- **TypeScript is pinned to 6.x, not 7.** The 7.x native port does not expose the
  JS compiler API (`ts.factory`), and `openapi-typescript` needs it. Revisit when
  the codegen ecosystem catches up.
- **`Microsoft.OpenApi` is pinned forward** to 2.11.0 via transitive pinning —
  the version ASP.NET Core pulls in has a DoS advisory (GHSA-v5pm-xwqc-g5wc).
- **The API runtime image is chiseled**: no shell, no `curl`. The Docker health
  check re-invokes the app binary with `--healthcheck`.
- `--export-openapi` must start the host before reading the document; endpoints
  do not reach the DI endpoint data source until then.
- **NuGet cache mounts use `sharing=locked`.** Compose builds the api and
  migrator images concurrently; with BuildKit's default `sharing=shared`, two
  `dotnet restore` runs corrupt each other's temp extractions in the shared
  global-packages folder.
- **Compose merges port lists across overlay files, it does not replace them.**
  `web` publishes no port in `compose.yaml`; each overlay declares its own, or
  production would also expose the dev port.
- **Integration tests configure the host with `UseSetting`, not
  `ConfigureAppConfiguration`.** Under minimal hosting the appsettings sources
  are appended afterwards and win, so the tests would silently hit
  `localhost:5432` instead of the Testcontainers instance.
- **The migrator image is `aspnet`, not `runtime`**, despite never serving a
  request: it references the modules, and `ICountflowModule.MapEndpoints` takes
  an `IEndpointRouteBuilder`, so SharedKernel has a FrameworkReference on
  `Microsoft.AspNetCore.App`. Splitting SharedKernel into core/web halves would
  let the migrator drop to the smaller base.
- **Seq will not start without an explicit auth decision.** Dev runs it open
  (`SEQ_FIRSTRUN_NOAUTHENTICATION`); prod requires `SEQ_ADMIN_PASSWORD`.
- **A console project does not publish `appsettings*.json`.** Only the Web SDK
  includes it implicitly, so `Countflow.Migrator` needs an explicit `<Content
  Include>`. Without it the file is absent from the image and every default in it
  silently does nothing — the migrator runs on environment variables alone and
  looks fine until someone adds a setting there.
- **`GSS Encryption Mode=Disable` on every connection string.** Npgsql defaults to
  `Prefer` and probes for Kerberos, which the chiseled image does not ship,
  printing a `Cannot load libgssapi_krb5` error on every start. We use SCRAM.
- **The migrator holds no JWT configuration.** `JwtOptions` is bound and validated
  in `AddCountflowJwtBearer`, which only the API calls — a host that never issues
  a token should not need a signing key to boot.
- **`EnableRetryOnFailure` forbids hand-rolled transactions.** Every module context
  enables it, and `NpgsqlRetryingExecutionStrategy` throws on
  `BeginTransactionAsync` — it cannot replay half a transaction. Anything that
  opens one must go through `Database.CreateExecutionStrategy().ExecuteAsync(…)`
  so the whole transaction is the retriable unit. `StockLedger` and
  `LedgerReconciler` both do; **clear the change tracker at the top of the
  delegate**, or a retry re-adds the entity the previous attempt already tracked.
- **Filter *and sort* before you project, in the module directories.**
  `Items.Select(…snapshot…).Where(s => s.Sku == sku)` leaves EF trying to
  translate a comparison against a constructor call and throws at runtime, and
  `.OrderBy(s => s.Code)` after a projection fails the same way — that one got
  through the tests and only surfaced when the migrator ran the demo seeders in
  a container. The `AsSnapshots`/`AsRows` helpers take an already-filtered,
  already-ordered `IQueryable` for exactly this reason.
- **EF cannot `Concat` two custom projections.** "Unable to translate set operation
  after client projection has been applied" — which is why `LedgerReplay` is
  hand-written `UNION ALL` SQL rather than LINQ. Summing the ledger in memory
  instead would work at seed scale and die on real history.
- **`AreNullsDistinct(false)` is load-bearing on the balance key.** Postgres treats
  every NULL as distinct by default, so without `NULLS NOT DISTINCT` a shelf holding
  a non-lot-tracked item grows a fresh balance row on every posting and no
  `ON CONFLICT` ever fires.
- **Testcontainers needs to pull `testcontainers/ryuk`, and a Docker credential
  helper can hang on it.** Symptom: `dotnet test` sits at 0% CPU forever with no
  Testcontainers container in `docker ps`. Common on macOS with a keychain-backed
  helper. Workaround is an isolated `DOCKER_CONFIG` containing `{"auths":{}}`:

  ```bash
  mkdir -p /tmp/docker-noauth && printf '{"auths":{}}' > /tmp/docker-noauth/config.json
  ln -sfn ~/.docker/cli-plugins /tmp/docker-noauth/cli-plugins   # see the next entry
  DOCKER_CONFIG=/tmp/docker-noauth dotnet test
  ```
- **An isolated `DOCKER_CONFIG` also hides the Docker CLI plugins**, because they
  are resolved from `$DOCKER_CONFIG/cli-plugins`. Symptom is a baffling
  `docker: unknown command: docker compose` from a daemon that is plainly
  running. Symlink `~/.docker/cli-plugins` into the isolated directory.
- **The Dockerfiles collect project files generically**, via a throwaway
  `projects` stage that tars every `*.csproj`. They used to list one `COPY` per
  project, which made "adding a module is one line in `ModuleCatalog`" quietly
  untrue — Phase 3 added three modules and the container build broke on all
  three, in a file nobody thinks to look in. The restore layer is keyed on the
  tar's contents, so it still only invalidates when a project file changes.
- **A pruned build cache breaks every cached publish layer, and blames NuGet for
  it.** Symptom: `docker compose build` fails with `NETSDK1064: Package
  Microsoft.AspNetCore.OpenApi, version 10.0.10 was not found. It might have been
  deleted since NuGet restore` — on a change that touched one line of C# and no
  package reference. The restore layer is cached in the image, but the packages it
  restored live in a **cache mount**, which is not part of the layer and which
  Docker's garbage collector will reclaim on its own schedule. `dotnet publish
  --no-restore` then finds a valid-looking restore and no packages. `--no-cache`
  on the affected images is the fix, and it must name every image whose publish
  layer went stale — `--no-cache api` will leave the migrator failing on the next
  `up`, since each project's chain is cached separately.
- **The integration fixture runs seeders, not just migrations.** From Phase 3 the
  putaway rule set is reference data the system cannot function without, so a
  fixture that only migrated would test a warehouse with no rules — a state
  production can never be in. Demo data stays off; `Seed:DemoData` is unset.
- **The `work` and `rules` schemas lean on filtered unique indexes.** One active
  rule set per decision kind per warehouse, and one open task per licence plate
  per origin, are both enforced by partial unique indexes rather than by the code
  that writes them — two receipts arriving together would otherwise both find
  nothing and both insert. The plate index is filtered to **putaway** as of
  Phase 4: a pick names a pallet too, but two orders drawing on one pallet are two
  jobs, and merging them would produce a confirmation that could only be right for
  one of them.
- **EF infers `Modified`, not `Added`, for a new entity reached through a tracked
  parent's navigation** when its key is already set — and every key here is a
  client-generated UUIDv7. `line.Allocations.Add(row)` on a loaded order therefore
  emits an `UPDATE` against a row that does not exist and fails with a
  `DbUpdateConcurrencyException` about affecting 0 rows. Add through the `DbSet`;
  the navigation is fixed up from the foreign key anyway. Adding children to a
  *new* parent is fine, which is why Phase 3 never hit it.
- **`InputsJson` and the trace blobs are `jsonb`, so `.Contains(...)` in a
  predicate fails** with `operator does not exist: jsonb ~~ jsonb`. Postgres has
  no `LIKE` for jsonb. Filter in memory, or add a proper jsonb operator.
- **A count row and its adjustment are written by the ledger, in one transaction.**
  `PostingCommand.RecordsCount` carries the entity rather than an id, because
  unlike a reservation the row does not exist yet — a clean count never reaches
  the ledger at all. A `CHECK` makes the pairing structural: variance zero if and
  only if there is no adjustment movement.
- **Derived idempotency keys got longer in Phase 4** — a wave's key begets an
  order's, which begets a line's, which begets one per holding. `StockReservation`,
  `WorkTaskSource` and `OrderLineAllocation` hold 256 characters rather than the
  128 a movement's key gets. Truncating to fit would break the very thing the
  chain exists for.

- **`ReplayRunDecision` rows are added through the `DbSet`, not the navigation** —
  the Phase 4 EF trap, unchanged: a child reached through a tracked parent whose
  client-generated key is already set is inferred `Modified`, not `Added`.
- **`RuleSetEditor.Apply` was the third instance, and it reached production.** It
  added rules through `ruleSet.Rules`, which is correct on `CreateDraftAsync` —
  the parent is new — and fatal on `ReplaceAsync`, where it is tracked. The
  console's Save button 500'd the first time anybody pressed it. **Six phases of
  tests missed it because every test of `ReplaceAsync` asserted a refusal, and
  every refusal returns before `SaveChangesAsync`.** A suite that only ever
  asserts refusals has not tested the method — that generalises well past this
  file, and it is worth checking for elsewhere.
- **Do not truncate a UUIDv7 for uniqueness.** A v7's leading hex digits are its
  millisecond timestamp, so `Guid.CreateVersion7().ToString("N")[..8]` collides
  for anything created in the same millisecond. A service account's operator code
  did exactly that and died on the unique index the second time two were created
  together. Use `RandomNumberGenerator` when you want randomness.
- **A framework-generated problem document has no `code` unless you put one
  there.** Every domain failure goes through `ToHttpResult()` and carries its
  `Error`; a missing credential, a missing role and a mistyped path are written by
  middleware that has never heard of `Error`, and came back bare. `docs/api/`
  tells integrators to branch on `code`, so `TransportErrorCodes` fills them in
  from `CustomizeProblemDetails` — the one point every problem document in the
  host passes through. It never overwrites an existing `code`.
- **A non-nullable string in a request contract is a comment, not a constraint —
  unless you turn the two options on.** `record LoginRequest(string Identifier,
  string Password)` reads as two required strings, and the OpenAPI export even
  publishes them as `"required"`, but System.Text.Json's defaults check neither the
  constructor's arity nor its nullable annotations: a body that omits `identifier`,
  or sends `"identifier": null`, binds it to `null` and the handler's first
  `Trim()` throws. Seven endpoints across five modules answered
  `http.unexpected_error` for a caller's typo. `RespectRequiredConstructorParameters`
  and `RespectNullableAnnotations`, both set in `Program.cs`, are what make the
  contract mean what it says — and they cost nothing here only because every
  optional parameter in this codebase already carries a default. **Give an optional
  parameter a default, always**, or these two options will make it required.
- **`ThrowOnBadRequest` defaults to on in Development, and it does not merely log
  louder.** A binding failure that would set `400` in production is instead
  rethrown as a `BadHttpRequestException`; `UseExceptionHandler` catches it,
  discards the status it carries, and writes `http.unexpected_error`. So
  `http.malformed_request` — documented since Phase 7 — had never once been emitted
  by the stack anybody runs, and malformed JSON, a mistyped field and a missing one
  were all `500`s under `docker compose up` while the table said `400`. Turned off
  in `Program.cs` so Development takes production's path rather than a second
  mapping beside it. **A contract only tested in the environment nobody develops
  in is not tested.**
- **The change feed serves behind a watermark, and that is its correctness.**
  Postgres assigns an identity value at `INSERT`, not at `COMMIT`, so two
  concurrent postings can take 105 and 106 and commit the other way round; a
  reader served 106 immediately would advance past 105 and never see it. The feed
  serves only up to the highest sequence whose `OccurredAt` is at least five
  seconds old. **Filter on sequence, never on the timestamp** — filtering on the
  timestamp reintroduces the same hole from the other side, because the row that
  lost the race can carry the later one.
- **The rate limiter partitions on the credential presented, not the identity it
  resolves to**, and therefore runs *before* `UseAuthentication`. Partitioning on
  identity would make every forged key cost a database lookup before anything
  decided whether to serve it — the denial-of-service vector the single-SHA-256
  hashing decision exists to avoid.
- **An API key is hashed with one SHA-256, deliberately.** The secret is 256 bits
  of CSPRNG output, so there is no dictionary to slow down, and a work-factored
  KDF on the authentication path of every request is something an unauthenticated
  caller can pull on us for the price of one HTTP request. Passwords are the
  opposite case in both respects and still go through Identity's hasher.
- **`VITE_`-prefixed values are inlined at build time**, which is why the demo's
  sign-in hints are a Docker build argument rather than an API call. An endpoint
  answering "which credentials work here" would exist in every deployment and be
  one misconfiguration from answering it in production.
- **The integration tests seed their own catalogue.** The fixture runs the seeders
  but leaves `Seed:DemoData` unset, so there are no items at all — the simulator
  gets "no items in the catalogue; nothing can be received" and returns an empty
  report. `ReplayTests.SeedCatalogueAsync` writes 24 ambient, lot-tracked SKUs
  across three velocity bands first.
- **The simulator swallows refusals and reports them.** A full aisle, a short pick,
  an order that cannot be packed are all information; a simulator that stopped at
  the first one would mostly be a test of the API's error handling. If a run comes
  back empty, read `SimulationReport.Notes` — it is the first place to look.
- **The simulator holds three credentials, and the admin one is load-bearing.**
  `ReplayTests` used to pass its *admin* client in the supervisor's slot, so nothing
  ever noticed that promoting a rule set is admin-only. From the command line, where
  the simulator really did sign in as a supervisor, the counting policy silently
  failed to promote and four simulated days produced **0 count plans and 0 counts** —
  a summary line nobody reads as a fault. A client whose credentials are stronger
  than the ones it claims to use is not testing the API an integrator has.
- **A dead API behind a reverse proxy answers 502; it does not fail to answer.**
  Caddy sits in front of the API, so `docker compose stop api` produces a
  *response* rather than a rejected `fetch`. Every screen that decided "we must
  be offline" by catching an exception therefore never decided it — and the app's
  session restore, seeing a non-2xx, signed the operator out and stranded their
  queued work on a device nobody could send it from. `unanswered(status)` in
  `lib/outbox/dispatch.ts` is exported for the *reads* for this reason: 5xx, 429
  and 408 are "nobody answered", and so is a thrown fetch. **A mock cannot show
  you this, because a mock has no proxy in front of it.**
- **The handheld's task cache has to be pruned, not merely added to.** Entries
  were written when a task was read and nothing ever took them out, so an
  operator was shown jobs the warehouse had finished with twenty minutes earlier
  — on a handheld that is an instruction to move a pallet somebody has already
  moved. `GET /work/tasks?mine=true&status=Assigned` is the authority
  (`reconcileTasks`), and what survives is what it lists **plus anything with a
  command still unsent**, because that entry is the device's own record of work
  in flight and is not the warehouse's to withdraw.
- **`/api/v1/auth/refresh`, and the one call in the client that is not typed.**
  `createCountflowClient` refreshes with a hand-written `fetch`, so Phase 7's
  move to `/api/v1` did not reach it and it went on calling `/api/auth/refresh`.
  That answers **404**, which the client reads as "the session is gone for good":
  every expiring token signed the user out instead of being refreshed, and the
  refresh-token rotation built in Phase 0 had never once run from a browser. On
  the handheld it meant a reload after a battery swap ended the shift's session
  with the operator's unsent work still on the device. Anything hand-written
  against a path is outside the drift check that keeps the rest honest.
- **A stalled command must have a way back.** `STALL_AFTER` stops the automatic
  retry so a fault cannot hold the shift up — but `drain` walks *pending*
  commands only, so before `Outbox.retry` a stalled one was unreachable by every
  automatic path and by the "send them now" button, and the single action on its
  card was the one that deletes it. Four minutes of 502s was enough to reach it.
  A stall means "stop holding the queue up", never "give up".
- **The reachability flag must not latch.** The badge reports whether the
  *warehouse* answered, which is right — and a drain only learns anything if it
  had something of the signed-in operator's to send. A device with an empty queue,
  or one holding only the previous operator's stranded work, therefore stayed
  "offline" on a working network with **"next job" disabled and no way back**.
  Three things fix it and all three are needed: a successful sign-in notes it, the
  Home refresh probes instead of gating on the flag it updates, and
  `startOutbox` heartbeats while the device believes itself unreachable.
- **A language detector that caches has overwritten the answer to a different
  question.** `i18next-browser-languagedetector`'s `caches: ['localStorage']`
  writes whatever it *detected* into the same key a deliberate choice is stored
  in — so "we guessed English from your browser" becomes indistinguishable from
  "this person chose English", and the app stops following the browser it was
  supposed to default to. It stored the raw navigator tag too, so the value was
  `en-US` rather than a supported code, which is the tell. Detection now writes
  nothing (`caches: []`), `setLanguage` is the only writer, and `initI18n` clears
  a stored value that is not an exact `SUPPORTED_LANGUAGES` entry — that shape
  can only have come from the old cache, so it is safe to treat as noise.
- **A pre-paint theme script must fall back to the system preference, not to a
  colour.** Reading `localStorage` is the part that throws — a locked-down device,
  private browsing — and a `catch` that assigns `'light'` (or `'dark'`) overrides
  the OS for exactly the people who never chose anything. Resolve `matchMedia`
  first and let the stored value override *it*. There are three copies of this
  script: `themeBootstrapScript` in `ThemeProvider.tsx`, which exists so the
  inline ones stay in sync, and the two `index.html` files, **which do not
  actually use it**. Change one, change all three until something inlines it.
- **A sticky scan field must ask *where* focus went, not merely whether it left.**
  `ScanField` holds focus so an operator with a scanner in one hand and a carton
  in the other never has to tap first, and it did that by refocusing on every
  `focusout`. Every screen with a scan field also has something to type beside it
  — quantity and dock on receiving, a task's override reason, and at the time the
  handheld's sign-in password — and all of them were unreachable: focus landed,
  then was pulled back a tick later by a `setTimeout(…, 0)`, which reads as a
  field that ignores taps rather than as a fight over focus. (Sign-in has since
  become an ordinary form; receiving and tasks still pair the two.) The rule is
  `acceptsTypedInput`
  (`packages/ui/src/lib/focus.ts`) against `relatedTarget`: focus that went
  nowhere comes back, focus the operator put in another *text* field stays. The
  case that fixes the shape is a **button** — focusable but untypeable, so "did
  focus move at all" would leave a scanner pointed at a control, where the Enter
  that ends every scan presses it.
- **A CSS custom property read in a mount effect can come back empty.** Under
  Vite the stylesheet is injected by JavaScript, so
  `getComputedStyle(root).getPropertyValue('--breakpoint-lg')` inside a
  `useEffect` returns `''` often enough to matter — and the symptom is not an
  error, it is a listener that silently never gets registered and a drawer that
  stays open when a tablet is rotated. Read tokens from CSS; where JavaScript
  genuinely needs one, make it a constant and hold the two together with a test,
  as `RAIL_BREAKPOINT` and `apps/console/test/navigation.test.mjs` do.
- **A Radix dialog opened by something that is not its `Trigger` does not restore
  focus.** Controlling `open` from a plain button beside it works for everything
  except the last step: on close Radix returns focus to `Dialog.Trigger`, and with
  no trigger to return to, a keyboard user who presses Escape lands on `<body>`
  and has to tab from the top of the document. `Sheet` takes the opener as a
  `trigger` prop and renders it inside `Dialog.Trigger` for exactly this reason —
  controlled `open` and a real trigger coexist fine, and the wrapper that
  hand-rolls the opener is the one that gets it wrong.
- **The in-app browser's viewport override dispatches nothing.** Resizing it
  changes `clientWidth` and flips `matchMedia(...).matches`, but fires no
  `matchMedia` change event, no `resize`, and no `ResizeObserver` callback. So
  anything that reacts to a breakpoint being *crossed* cannot be provoked there
  and has to be checked in a real browser — the harness cannot ask the question,
  which is not the same as the answer being no.
- **Only send a lot where the item has lots.** The ledger refuses
  `inventory.lot_not_permitted`, and the simulator attached a batch to everything.
  Invisible against the fixture catalogue, which is lot-tracked throughout; against
  the shipped 500-SKU seed, where roughly two ambient items in seven carry batches,
  it silently threw away 11 receipts of 28. A lot number is also **a batch**, so its
  expiry is drawn once and remembered — two deliveries of `L812-0102` claiming
  different expiry dates is a 409, and correctly so.
