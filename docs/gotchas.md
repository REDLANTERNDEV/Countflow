# Gotchas

Things that behave differently from how they read, in .NET, EF Core, PostgreSQL,
Docker, Vite and the browser. Symptom first, because that is what you will have
when you come looking. Phase numbers refer to [history.md](history.md).

**If you lose an afternoon to something that is not here, add it.**

---

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
- **Only send a lot where the item has lots.** The ledger refuses
  `inventory.lot_not_permitted`, and the simulator attached a batch to everything.
  Invisible against the fixture catalogue, which is lot-tracked throughout; against
  the shipped 500-SKU seed, where roughly two ambient items in seven carry batches,
  it silently threw away 11 receipts of 28. A lot number is also **a batch**, so its
  expiry is drawn once and remembered — two deliveries of `L812-0102` claiming
  different expiry dates is a 409, and correctly so.
