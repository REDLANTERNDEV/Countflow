using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

/// <summary>
/// Phase 7: what an outside integrator can actually do.
/// <para>
/// The three things this phase adds are a machine credential, a change feed and a
/// rate limit, and the questions worth asking of them are not the ones the other
/// test classes ask. Everywhere else in this suite the interesting failure is a
/// wrong number; here it is a <i>credential doing something it should not</i>, or a
/// consumer silently missing a change and never being told.
/// </para>
/// <para>
/// The load-bearing claim is that there is <b>no second authorization
/// vocabulary</b>: a key carries its account's roles and is judged by the same
/// <c>RequireRole</c> a person is. Several tests below exist only to show that an
/// endpoint behaves identically for the two credentials, which is the sort of test
/// that looks redundant right up until somebody adds a scope.
/// </para>
/// </summary>
public sealed class IntegrationSurfaceTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string Secret = "Countflow#2026";

    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    private HttpClient _admin = null!;
    private HttpClient _supervisor = null!;
    private List<string> _bins = [];

    /// <summary>
    /// Static, because xUnit constructs the class once per test: an instance
    /// counter restarts at zero for every one of them and hands the second test
    /// the first test's SKU.
    /// </summary>
    private static int _counter;

    public async Task InitializeAsync()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

            if (!await masterData.Warehouses.AnyAsync())
            {
                var layout = new WarehouseLayoutBuilder().Build();
                masterData.Warehouses.Add(layout.Warehouse);
                masterData.Areas.AddRange(layout.Areas);
                masterData.Zones.AddRange(layout.Zones);
                masterData.Locations.AddRange(layout.Locations);
                await masterData.SaveChangesAsync();
            }

            _bins = await masterData.Locations
                .Where(location => location.Type == LocationType.PickFace || location.Type == LocationType.Bulk)
                .OrderBy(location => location.Code)
                .Select(location => location.Code)
                .Take(8)
                .ToListAsync();

            await EnsureUsersAsync(scope.ServiceProvider);
        }

        _admin = await SignInAsync("surface-admin");
        _supervisor = await SignInAsync("surface-supervisor");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------ the credential

    [Fact]
    public async Task A_key_authenticates_the_same_endpoints_a_person_reaches()
    {
        var (_, client) = await NewIntegrationAsync(Roles.Supervisor);

        var response = await client.GetAsync("/api/v1/warehouses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Me_tells_an_integrator_who_they_are_and_that_they_are_a_machine()
    {
        var (account, client) = await NewIntegrationAsync(Roles.Supervisor);

        var me = await client.GetFromJsonAsync<UserInfoBody>("/api/v1/auth/me");

        Assert.NotNull(me);
        Assert.Equal(account.Id, me.Id);
        Assert.True(me.IsServiceAccount);
        Assert.Contains(Roles.Supervisor, me.Roles);
    }

    [Fact]
    public async Task A_key_carries_its_accounts_roles_and_nothing_more()
    {
        // The whole argument for not inventing scopes, in one test. Promotion is
        // admin-only; the operator-role key is refused by the identical
        // RequireRole that refuses a human operator, and the admin-role key is
        // allowed by the identical one that allows a human admin. Neither endpoint
        // knows which kind of credential it is looking at.
        var (_, restricted) = await NewIntegrationAsync(Roles.Operator);
        var (_, privileged) = await NewIntegrationAsync(Roles.Admin);

        var draft = await CloneActivePutawayDraftAsync();

        var refused = await restricted.PostAsync($"/api/v1/rules/sets/{draft}/promote", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var allowed = await privileged.PostAsync($"/api/v1/rules/sets/{draft}/promote", content: null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task A_service_account_cannot_log_in_with_a_password()
    {
        var (account, _) = await NewIntegrationAsync(Roles.Admin);

        // There is no password to guess — the account was created without one — so
        // this is checking the refusal states the rule rather than relying on the
        // absence. Any password, including the one every other test user has.
        var response = await _admin.PostAsJsonAsync(
            "/api/v1/auth/login", Credentials(account.Name, Secret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_human_cannot_be_given_a_key()
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CountflowUser>>();
        var human = await users.FindByNameAsync("surface-supervisor");
        Assert.NotNull(human);

        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/auth/service-accounts/{human.Id}/keys", new { name = "should not exist" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("identity.not_a_service_account", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_secret_is_shown_once_and_is_not_readable_afterwards()
    {
        var (account, _) = await NewIntegrationAsync(Roles.Operator);

        var listed = await _admin.GetFromJsonAsync<ApiKeyBody[]>(
            $"/api/v1/auth/service-accounts/{account.Id}/keys");

        Assert.NotNull(listed);
        var key = Assert.Single(listed);

        // The prefix identifies; it does not authenticate. Presenting it as if it
        // were the secret must get nowhere, which is the property that makes it
        // safe to show in a list.
        Assert.StartsWith("cfk_", key.Prefix);

        using var impostor = fixture.CreateClient();
        impostor.DefaultRequestHeaders.Add("X-Api-Key", key.Prefix);

        Assert.Equal(HttpStatusCode.Unauthorized, (await impostor.GetAsync("/api/v1/warehouses")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_stops_working_and_is_still_on_the_record()
    {
        var (account, client) = await NewIntegrationAsync(Roles.Supervisor);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/warehouses")).StatusCode);

        var keys = await _admin.GetFromJsonAsync<ApiKeyBody[]>(
            $"/api/v1/auth/service-accounts/{account.Id}/keys");

        var key = Assert.Single(keys!);

        var revoked = await _admin.PostAsync(
            $"/api/v1/auth/service-accounts/keys/{key.Id}/revoke", content: null);

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/warehouses")).StatusCode);

        // Never deleted. A movement posted by this integration in March names the
        // account, and the key it was posted with has to remain answerable for.
        var after = await _admin.GetFromJsonAsync<ApiKeyBody[]>(
            $"/api/v1/auth/service-accounts/{account.Id}/keys");

        Assert.Single(after!);
        Assert.NotNull(after![0].RevokedAt);
    }

    [Fact]
    public async Task A_key_cannot_mint_another_key_even_holding_admin()
    {
        var (account, client) = await NewIntegrationAsync(Roles.Admin);

        // The role check passes — this credential really is an administrator. What
        // refuses it is the credential invariant one floor up: a leaked key must
        // not be able to issue itself a permanent replacement, or revoking the one
        // that leaked would not actually cut the integration off.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/auth/service-accounts/{account.Id}/keys", new { name = "a second one" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("identity.human_credential_required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_key_may_revoke_itself_because_burning_a_leak_should_never_wait()
    {
        var (account, client) = await NewIntegrationAsync(Roles.Admin);

        var keys = await _admin.GetFromJsonAsync<ApiKeyBody[]>(
            $"/api/v1/auth/service-accounts/{account.Id}/keys");

        var response = await client.PostAsync(
            $"/api/v1/auth/service-accounts/keys/{keys![0].Id}/revoke", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/warehouses")).StatusCode);
    }

    [Fact]
    public async Task An_expired_key_is_refused()
    {
        var (account, _) = await NewIntegrationAsync(Roles.Supervisor);

        var issued = await IssueKeyAsync(account.Id, "already over", expiresInDays: 1);

        using var scope = fixture.Services.CreateScope();
        var identity = scope.ServiceProvider
            .GetRequiredService<Modules.Identity.Persistence.IdentityModuleDbContext>();

        // Aged rather than clock-faked, for the reason the counting tests age
        // StockBalance.UpdatedAt: a fake clock has to be agreed on by every module
        // that reads one, and this is a single column with a single reader.
        await identity.ApiKeys
            .Where(key => key.Id == issued.Key.Id)
            .ExecuteUpdateAsync(key => key.SetProperty(
                row => row.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", issued.Secret);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/warehouses")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_key_is_refused_without_falling_back_to_the_bearer_token()
    {
        // The forwarder chooses on the *presence* of the header, not on whether it
        // works. A caller sending both a good token and a bad key must be refused —
        // silently falling through to the token would mean a revoked integration
        // kept working for anyone who still had a session open.
        using var client = fixture.CreateClient();
        var tokens = await TokensAsync("surface-admin");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens);
        client.DefaultRequestHeaders.Add("X-Api-Key", "cfk_not-a-real-key-at-all");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/warehouses")).StatusCode);
    }

    [Fact]
    public async Task Issuing_credentials_is_an_administrators_job()
    {
        var response = await _supervisor.PostAsJsonAsync(
            "/api/v1/auth/service-accounts",
            new { name = "supervisor-should-not", displayName = "No", roles = new[] { Roles.Operator } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("auth.credential_required")]
    [InlineData("auth.role_required")]
    [InlineData("http.not_found")]
    [InlineData("http.malformed_request")]
    public async Task Every_refusal_carries_a_code_including_the_ones_no_handler_wrote(string expected)
    {
        // docs/api/ tells an integrator to branch on `code` and never on prose,
        // and that promise used to hold for every failure *except the first three
        // they would hit*: a missing credential, a role they do not have, and a
        // mistyped path are all written by middleware that knows nothing about the
        // domain's Error type. Driving the surface by hand in Phase 7 found bare
        // problem documents there. A contract that teaches the right lesson on the
        // happy path and breaks on a Friday is worse than no contract.
        //
        // The fourth case is the one TransportErrorCodes always named and this
        // theory never asked for — a body that would not bind — and leaving it out
        // is how it stayed a 500 under `docker compose up` for four phases while
        // the table said 400. It is asked of a *documented* endpoint here, so a
        // route that does not exist cannot answer for one that does.
        var response = expected switch
        {
            "auth.credential_required" => await fixture.CreateClient().GetAsync("/api/v1/warehouses"),
            "auth.role_required" => await (await SignInAsync("surface-supervisor"))
                .PostAsJsonAsync("/api/v1/auth/service-accounts", new { name = "x", displayName = "x", roles = new[] { "Operator" } }),
            "http.malformed_request" => await _admin.PostAsJsonAsync("/api/v1/inventory/receipts", new { }),
            _ => await _admin.GetAsync("/api/v1/no-such-endpoint")
        };

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(expected, document.RootElement.GetProperty("code").GetString());
    }

    // ---------------------------------------------------- the acceptance test

    [Fact]
    public async Task An_integration_drives_both_warehouse_loops_on_machine_credentials_alone()
    {
        // The phase's acceptance test, and the reason the simulator is a client
        // rather than a module (ADR 0006). It is the only thing in this repository
        // that consumes the API the way an outsider does — it holds no project
        // references and cannot reach past a boundary even by accident — so if it
        // can book deliveries, do the jobs, ship the orders and count the racking
        // holding nothing but three API keys, an integrator can.
        //
        // Three accounts rather than one, with the roles split exactly as the
        // command-line simulator splits them. That is load-bearing: promoting a
        // rule set is admin-only, and it was a *stronger-than-claimed* credential
        // in the supervisor slot that hid that fact for a whole phase. One
        // all-powerful service account here would hide it again.
        var (_, supervisor) = await NewIntegrationAsync(Roles.Supervisor);
        var (_, worker) = await NewIntegrationAsync(Roles.Operator);
        var (_, administrator) = await NewIntegrationAsync(Roles.Admin);

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var warehouse = await masterData.Warehouses.Select(row => row.Code).FirstAsync();

        var simulator = new Simulator.WarehouseSimulator(supervisor, worker, administrator);

        // Small on purpose. The question here is whether the surface admits an
        // integration at all, not whether the warehouse looks realistic — that is
        // ReplayTests' question, and it runs a much longer one to ask it.
        var report = await simulator.RunAsync(new Simulator.SimulationPlan(
            warehouse, Days: 1, ReceiptsPerDay: 2, OrdersPerDay: 1, CountsPerDay: 3, SkuPoolSize: 12));

        Assert.True(report.Receipts > 0, Explain("nothing was received", report));
        Assert.True(report.Putaways > 0, Explain("nothing was put away", report));
        Assert.True(report.Orders > 0, Explain("no order was released", report));
        Assert.True(report.Picks > 0, Explain("nothing was picked", report));

        // The admin key specifically. Without a promotion the quiet period stands,
        // and a warehouse built ninety seconds ago has nothing countable — which is
        // exactly the silence that hid the missing admin credential last phase, so
        // it is asserted rather than left to a summary line nobody reads.
        Assert.True(report.CountPlans > 0, Explain("the counting policy never got promoted", report));
        Assert.True(report.Counts > 0, Explain("no count was completed", report));

        static string Explain(string what, Simulator.SimulationReport report) =>
            $"{what} — {report}\n{string.Join("\n", report.Notes.Take(10))}";
    }

    // -------------------------------------------------------------- the feed

    [Fact]
    public async Task The_feed_hands_out_every_change_exactly_once_in_order()
    {
        var sku = await NewItemAsync();

        var start = await CursorAsync();

        for (var i = 0; i < 7; i++)
        {
            await SucceededAsync(await ReceiveAsync(sku, 10m, _bins[i % _bins.Count]));
        }

        // Walked in pages of two, deliberately: a feed that is only ever read in
        // one gulp never exercises its own cursor, and the cursor is the whole
        // contract.
        var seen = new List<long>();
        var cursor = start;

        await SettleAsync();

        while (true)
        {
            var page = await _supervisor.GetFromJsonAsync<ChangePageBody>(
                $"/api/v1/inventory/changes?afterSequence={cursor}&limit=2");

            Assert.NotNull(page);
            seen.AddRange(page.Changes.Select(change => change.Sequence));

            cursor = page.NextCursor;

            if (!page.HasMore)
            {
                break;
            }
        }

        var mine = seen.Where(sequence => sequence > start).ToList();

        Assert.Equal(mine.Count, mine.Distinct().Count());
        Assert.Equal(mine, [.. mine.OrderBy(sequence => sequence)]);

        // Seven receipts, and no other test's movements missing from the middle:
        // the feed is filtered by nothing, which is what makes a cursor over it a
        // complete record rather than a sample.
        var ledger = await SequencesAboveAsync(start, cursor);
        Assert.Equal(ledger, mine);
    }

    [Fact]
    public async Task An_empty_page_does_not_move_the_cursor()
    {
        // A watermark can advance between requests as earlier receipts age
        // through the safety lag. Start above every committed movement so this
        // page is actually empty regardless of that clock boundary.
        using var scope = fixture.Services.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<Modules.Inventory.Persistence.InventoryDbContext>();
        var cursor = await inventory.StockMovements.MaxAsync(movement => (long?)movement.Sequence) ?? 0;

        var page = await _supervisor.GetFromJsonAsync<ChangePageBody>(
            $"/api/v1/inventory/changes?afterSequence={cursor}&limit=50");

        Assert.NotNull(page);
        Assert.Empty(page.Changes);
        Assert.Equal(cursor, page.NextCursor);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task The_feed_holds_back_a_movement_until_no_earlier_one_can_still_arrive()
    {
        // The reason this endpoint is not simply "order by sequence". Postgres
        // assigns a sequence at INSERT and not at COMMIT, so two concurrent
        // postings can take 105 and 106 and commit the other way round; a reader
        // served 106 immediately would advance past 105 and never see it. The feed
        // therefore serves only up to a watermark five seconds behind the newest
        // entry, by which time every lower sequence has settled.
        var sku = await NewItemAsync();
        var before = await CursorAsync();

        var stopwatch = Stopwatch.StartNew();

        await SucceededAsync(await ReceiveAsync(sku, 5m, _bins[0]));

        var immediately = await _supervisor.GetFromJsonAsync<ChangePageBody>(
            $"/api/v1/inventory/changes?afterSequence={before}&limit=50");

        // Guarded rather than asserted flat. If this machine took seconds to get
        // from the post to the read, the lag has legitimately elapsed and the
        // negative assertion would be measuring the test host rather than the feed.
        if (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            // Earlier receipts can cross the lag boundary between requests;
            // this guard concerns the movement this test just posted.
            Assert.DoesNotContain(immediately!.Changes, change => change.Sku == sku);
        }

        await SettleAsync();

        var afterwards = await _supervisor.GetFromJsonAsync<ChangePageBody>(
            $"/api/v1/inventory/changes?afterSequence={before}&limit=50");

        Assert.Contains(afterwards!.Changes, change => change.Sku == sku);
    }

    [Fact]
    public async Task The_feed_needs_a_credential_like_everything_else()
    {
        using var anonymous = fixture.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/inventory/changes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------- the rate limit

    [Fact]
    public async Task A_credential_over_its_limit_is_refused_and_its_neighbour_is_not()
    {
        // Its own host, with the limiter on and turned down to something a test can
        // actually exceed. The shared fixture runs with it off — the suite's whole
        // traffic pattern is what the limiter exists to refuse, and leaving it on
        // there would make an unrelated test fail whenever somebody added a few.
        using var limited = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Enabled", "true");
            builder.UseSetting("RateLimiting:CredentialPermitsPerWindow", "6");
            builder.UseSetting("RateLimiting:Window", "00:01:00");
        });

        var tokens = await TokensAsync("surface-admin");

        using var noisy = limited.CreateClient();
        noisy.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await noisy.GetAsync("/api/v1/warehouses")).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        var refused = await noisy.GetAsync("/api/v1/warehouses");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Contains("rate_limit.exceeded", await refused.Content.ReadAsStringAsync());

        // The partition is the credential, not the process, the address or the
        // endpoint. One integration flooding the API must not take the others down
        // with it — which on a shared demo host is the entire point.
        var (_, quiet) = await NewIntegrationAsync(Roles.Supervisor, limited);
        Assert.Equal(HttpStatusCode.OK, (await quiet.GetAsync("/api/v1/warehouses")).StatusCode);
    }

    [Fact]
    public async Task Health_probes_are_never_rate_limited()
    {
        // Docker re-invokes the API binary against /health/ready every few seconds.
        // A container that gets itself killed for being too healthy is a poor trade.
        using var limited = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Enabled", "true");
            builder.UseSetting("RateLimiting:AnonymousPermitsPerWindow", "2");
        });

        using var client = limited.CreateClient();

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// <c>EnsureSuccessStatusCode</c> with the body in the message. A failure here
    /// is a coded <c>Error</c> that says exactly what went wrong, and throwing it
    /// away to report "500" costs a debugging round trip every time.
    /// </summary>
    private static async Task SucceededAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: "
                + await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// Waits out the change feed's stability lag. Slow on purpose and used in
    /// exactly two tests, because the lag is the mechanism rather than an
    /// inconvenience — see <c>GetLedgerChanges.StabilityLag</c>.
    /// </summary>
    private static Task SettleAsync() => Task.Delay(TimeSpan.FromSeconds(6));

    private async Task<HttpResponseMessage> ReceiveAsync(string sku, decimal quantity, string locationCode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/receipts")
        {
            Content = JsonContent.Create(new { sku, quantity, toLocationCode = locationCode })
        };

        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        return await _supervisor.SendAsync(request);
    }

    private async Task<long> CursorAsync()
    {
        var page = await _supervisor.GetFromJsonAsync<ChangePageBody>(
            "/api/v1/inventory/changes?afterSequence=0&limit=1");

        // The watermark, not the last row returned: everything at or below it has
        // already been handed out or is about to be, and a test that started from
        // row one would be reading every other test class's movements.
        return page!.Watermark;
    }

    private async Task<List<long>> SequencesAboveAsync(long after, long upTo)
    {
        using var scope = fixture.Services.CreateScope();
        var inventory = scope.ServiceProvider
            .GetRequiredService<Modules.Inventory.Persistence.InventoryDbContext>();

        return await inventory.StockMovements
            .AsNoTracking()
            .Where(movement => movement.Sequence > after && movement.Sequence <= upTo)
            .OrderBy(movement => movement.Sequence)
            .Select(movement => movement.Sequence)
            .ToListAsync();
    }

    private async Task<Guid> CloneActivePutawayDraftAsync()
    {
        var sets = await _admin.GetFromJsonAsync<RuleSetsBody>("/api/v1/rules/sets?kind=Putaway");
        var active = sets!.Items.First(set => set.Status == "Active");

        var created = await _admin.PostAsJsonAsync("/api/v1/rules/sets", new
        {
            kind = "Putaway",
            name = $"surface draft {Interlocked.Increment(ref _counter)}",
            cloneFromRuleSetId = active.Id
        });

        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<RuleSetBody>())!.Id;
    }

    private async Task<(ServiceAccountBody Account, HttpClient Client)> NewIntegrationAsync(
        string role,
        WebApplicationFactory<Program>? host = null,
        [CallerMemberName] string caller = "")
    {
        var name = $"svc-{Interlocked.Increment(ref _counter)}-{caller.ToLowerInvariant()[..Math.Min(caller.Length, 20)]}";

        var created = await _admin.PostAsJsonAsync("/api/v1/auth/service-accounts", new
        {
            name,
            displayName = $"Integration for {caller}",
            roles = new[] { role }
        });

        await SucceededAsync(created);
        var account = (await created.Content.ReadFromJsonAsync<ServiceAccountBody>())!;

        var issued = await IssueKeyAsync(account.Id, "primary");

        var client = host is null ? fixture.CreateClient() : host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", issued.Secret);

        return (account, client);
    }

    private async Task<CreatedApiKeyBody> IssueKeyAsync(Guid accountId, string name, int? expiresInDays = null)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/auth/service-accounts/{accountId}/keys", new { name, expiresInDays });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<CreatedApiKeyBody>())!;
    }

    private async Task<string> TokensAsync(string userName)
    {
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(userName, Secret));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<LoginBody>())!.AccessToken;
    }

    private async Task<HttpClient> SignInAsync(string userName)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await TokensAsync(userName));

        return client;
    }

    private async Task<string> NewItemAsync([CallerMemberName] string caller = "")
    {
        var sku = $"SURFACE-{Interlocked.Increment(ref _counter):D3}";

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var item = new Item
        {
            Sku = sku,
            Description = $"Integration surface item for {caller}",
            BaseUom = "EA"
        };

        item.PackConfigurations.Add(new PackConfiguration
        {
            ItemId = item.Id,
            Level = PackagingLevel.Each,
            BaseUnitQuantity = 1,
            IsPickUnit = true
        });

        masterData.Items.Add(item);
        await masterData.SaveChangesAsync();

        return sku;
    }

    private async Task EnsureUsersAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<CountflowRole>>();
        var users = services.GetRequiredService<UserManager<CountflowUser>>();

        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new CountflowRole(role));
            }
        }

        await EnsureAsync("surface-admin", "SRF-ADM", Roles.Admin);
        await EnsureAsync("surface-supervisor", "SRF-SUP", Roles.Supervisor);

        async Task EnsureAsync(string userName, string operatorCode, string role)
        {
            if (await users.FindByNameAsync(userName) is not null)
            {
                return;
            }

            var user = new CountflowUser
            {
                UserName = userName,
                Email = $"{userName}@countflow.test",
                EmailConfirmed = true,
                DisplayName = userName,
                OperatorCode = operatorCode
            };

            var created = await users.CreateAsync(user, Secret);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

            await users.AddToRoleAsync(user, role);
        }
    }

    // ------------------------------------------------------------- wire shapes

    private sealed record LoginBody(string AccessToken);

    private sealed record UserInfoBody(Guid Id, string UserName, string[] Roles, bool IsServiceAccount);

    private sealed record ServiceAccountBody(Guid Id, string Name, string[] Roles);

    private sealed record ApiKeyBody(Guid Id, string Name, string Prefix, DateTimeOffset? RevokedAt);

    private sealed record CreatedApiKeyBody(ApiKeyBody Key, string Secret);

    private sealed record ChangeBody(long Sequence, string Sku, decimal Quantity);

    private sealed record ChangePageBody(
        IReadOnlyList<ChangeBody> Changes,
        long NextCursor,
        bool HasMore,
        long Watermark);

    private sealed record RuleSetBody(Guid Id, string Status);

    private sealed record RuleSetsBody(IReadOnlyList<RuleSetBody> Items);
}
