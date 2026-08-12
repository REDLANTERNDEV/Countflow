using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Countflow.Simulator;

/// <summary>
/// Runs a warehouse forwards until it looks like one.
/// <para>
/// <b>Why this exists.</b> Three preference-scoring flaws have been found in this
/// system so far — allocation's travel rule and FEFO in Phase 4, counting's travel
/// rule in Phase 5 — and none of them was found by the test suite. All three were
/// found by a person driving a phase through the containers and reading a trace
/// that looked wrong. All three were about the same thing: what a <i>realistic
/// candidate field</i> does to a normaliser. A unit test builds two bins and a
/// pallet, and two bins and a pallet is exactly the field in which a broken
/// normaliser and a working one agree.
/// </para>
/// <para>
/// So this makes the warehouse the tests could not: hundreds of holdings spread
/// across real racking, the same SKU in several bays, stock that has been picked
/// down and topped up, batches at different ages, operators who occasionally put a
/// pallet somewhere else, and counts that mostly agree and sometimes do not. Every
/// decision the system makes along the way is traced, and every trace is something
/// replay and the influence report can be pointed at.
/// </para>
/// <para>
/// <b>It is a client, not a module.</b> It signs in and uses the endpoints a
/// handheld and a console use, so it cannot reach past a module boundary even by
/// accident, and everything it manages to do is by definition something a real
/// operator could have done. Written as a module it would have needed project
/// references to Inbound, Outbound and Counting — none of which publishes anything
/// — and would have been the first thing in the codebase allowed to ignore
/// ADR 0002.
/// </para>
/// </summary>
public sealed class WarehouseSimulator(
    HttpClient supervisor,
    HttpClient operatorClient,
    Action<string>? log = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<string> _notes = [];

    public async Task<SimulationReport> RunAsync(SimulationPlan plan, CancellationToken cancellationToken = default)
    {
        var random = new Random(plan.Seed);

        var dock = await FindDockAsync(cancellationToken);
        if (dock is null)
        {
            Note("no receiving dock in this warehouse; nothing can be received");
            return SimulationReport.Empty with { Notes = _notes };
        }

        var bench = await FindBenchAsync(cancellationToken);

        var skus = await FindSkusAsync(plan.SkuPoolSize, cancellationToken);
        if (skus.Count == 0)
        {
            Note("no items in the catalogue; nothing can be received");
            return SimulationReport.Empty with { Notes = _notes };
        }

        if (plan.LowerCountQuietPeriod)
        {
            await OpenTheQuietPeriodAsync(plan.WarehouseCode, cancellationToken);
        }

        var totals = new Totals();
        var putAway = new List<string>();

        for (var day = 1; day <= plan.Days; day++)
        {
            Report($"day {day} of {plan.Days}");

            for (var receipt = 0; receipt < plan.ReceiptsPerDay; receipt++)
            {
                await ReceiveAsync(plan, random, skus, dock, day, receipt, totals, putAway, cancellationToken);
            }

            await WorkTheQueueAsync("Putaway", plan, random, totals, cancellationToken);

            // Orders only against stock this run has actually put away. Ordering a
            // SKU at random would mostly produce shortfalls, and a warehouse full of
            // unfillable orders exercises the same code path over and over while
            // making no allocation decisions worth replaying.
            for (var order = 0; order < plan.OrdersPerDay && putAway.Count > 0; order++)
            {
                await ShipAsync(plan, random, putAway, bench, day, order, totals, cancellationToken);
            }

            await WorkTheQueueAsync("Pick", plan, random, totals, cancellationToken);

            await CountAsync(plan, random, day, totals, cancellationToken);
        }

        var report = new SimulationReport(
            totals.Receipts,
            totals.Putaways,
            totals.Overrides,
            totals.Orders,
            totals.Short,
            totals.Picks,
            totals.Despatches,
            totals.CountPlans,
            totals.Counts,
            totals.Variances,
            _notes);

        Report(report.ToString());

        return report;
    }

    // ------------------------------------------------------------- the loops

    private async Task ReceiveAsync(
        SimulationPlan plan,
        Random random,
        IReadOnlyList<string> skus,
        string dock,
        int day,
        int index,
        Totals totals,
        List<string> putAway,
        CancellationToken cancellationToken)
    {
        var reference = $"SIM-ASN-{plan.Seed}-{day:D2}{index:D2}";

        var lines = Enumerable.Range(0, plan.LinesPerReceipt)
            .Select(_ => new
            {
                sku = skus[random.Next(skus.Count)],
                expectedQuantity = (decimal)(random.Next(4, 25) * 6),
                expectedLotNumber = $"L{plan.Seed % 1000:D3}-{day:D2}{random.Next(1, 4):D2}",
                // Batches at a spread of ages, which is the input FEFO exists to
                // rank and the reason a warehouse built entirely today would tell
                // rotation rules nothing.
                expectedExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(random.Next(30, 720)))
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            })
            .DistinctBy(line => new { line.sku, line.expectedLotNumber })
            .ToList();

        var asn = await PostAsync<AsnView>(
            supervisor,
            "/api/inbound/asns",
            new { reference, warehouseCode = plan.WarehouseCode, lines, supplierName = "Simulated Supplies Ltd" },
            cancellationToken);

        if (asn is null)
        {
            return;
        }

        foreach (var line in asn.Lines)
        {
            // Short and over deliveries, because a truck that always brings exactly
            // what the paperwork said is not a truck.
            var delivered = line.ExpectedQuantity + (random.NextDouble() < 0.15 ? random.Next(-12, 13) : 0);
            if (delivered <= 0) continue;

            var posted = await PostAsync<JsonElement?>(
                operatorClient,
                "/api/inbound/receipts",
                new
                {
                    sku = line.Sku,
                    quantity = delivered,
                    toLocationCode = dock,
                    asnReference = asn.Reference,
                    asnLineNumber = line.LineNumber,
                    lotNumber = line.ExpectedLotNumber,
                    expiryDate = line.ExpectedExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                },
                cancellationToken);

            if (posted is not null)
            {
                totals.Receipts++;
                putAway.Add(line.Sku);
            }
        }
    }

    /// <summary>
    /// Does every job of one kind that is waiting, the way an operator does: claim
    /// the next one, go where it says, confirm.
    /// <para>
    /// The override is the interesting part. A confirmation somewhere other than the
    /// suggestion is a real thing operators do and the signal the rule set is wrong
    /// about something, and a simulated warehouse in which it never happens produces
    /// an override rate of exactly zero — which is the one number on the accuracy
    /// dashboard that would then be quietly untested.
    /// </para>
    /// </summary>
    private async Task WorkTheQueueAsync(
        string kind,
        SimulationPlan plan,
        Random random,
        Totals totals,
        CancellationToken cancellationToken)
    {
        // Bounded rather than "until empty". A bug that stopped tasks completing
        // would otherwise turn the simulator into an infinite loop, and a test that
        // hangs is far harder to read than one that fails.
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var claimed = await PostAsync<TaskDetailView>(
                operatorClient,
                "/api/work/tasks/claim-next",
                new { kind },
                cancellationToken);

            if (claimed?.Task is not { } task)
            {
                return;
            }

            var destination = task.SuggestedLocationCode;

            if (destination is null)
            {
                // Nowhere to put it. Real, and the trace explains why — but nothing
                // an operator can confirm, so it goes back on the queue.
                await PostAsync<JsonElement?>(
                    operatorClient, $"/api/work/tasks/{task.Id}/release", new { }, cancellationToken);

                Note($"{kind} {task.Sku} had no admissible location and was left on the queue");
                continue;
            }

            var overridden = kind == "Putaway"
                             && random.NextDouble() < plan.OverrideRate
                             && await FindOtherStorageAsync(destination, random, cancellationToken) is { } elsewhere
                ? elsewhere
                : null;

            var confirmed = await PostAsync<JsonElement?>(
                operatorClient,
                $"/api/work/tasks/{task.Id}/complete",
                new
                {
                    toLocationCode = overridden ?? destination,
                    overrideReason = overridden is null ? null : "sim.operator_judgement"
                },
                cancellationToken);

            if (confirmed is null)
            {
                continue;
            }

            if (kind == "Putaway")
            {
                totals.Putaways++;
                if (overridden is not null) totals.Overrides++;
            }
            else
            {
                totals.Picks++;
            }
        }

        Note($"stopped working the {kind} queue after 500 jobs; something is not completing");
    }

    private async Task ShipAsync(
        SimulationPlan plan,
        Random random,
        IReadOnlyList<string> putAway,
        string? bench,
        int day,
        int index,
        Totals totals,
        CancellationToken cancellationToken)
    {
        var reference = $"SIM-SO-{plan.Seed}-{day:D2}{index:D2}";

        var lines = Enumerable.Range(0, plan.LinesPerOrder)
            .Select(_ => new { sku = putAway[random.Next(putAway.Count)], quantity = (decimal)random.Next(6, 60) })
            .DistinctBy(line => line.sku)
            .ToList();

        var order = await PostAsync<OrderView>(
            supervisor,
            "/api/outbound/orders",
            new
            {
                warehouseCode = plan.WarehouseCode,
                reference,
                lines,
                customerName = "Simulated Retail plc",
                stagingLocationCode = bench
            },
            cancellationToken);

        if (order is null) return;

        totals.Orders++;

        var released = await PostAsync<ReleaseView>(
            supervisor, $"/api/outbound/orders/{order.Id}/release", new { }, cancellationToken);

        if (released is null) return;

        if (released.ShortfallQuantity > 0m) totals.Short++;

        // The picks are worked by the queue loop that follows, so the order is
        // packed and despatched on the next pass rather than here. Both are
        // deliberately left to fail quietly when a line went short: an order that
        // could not be filled is a real state a warehouse sits in, and forcing every
        // simulated order all the way to a trailer would remove exactly the
        // half-finished stock that makes the next day's decisions interesting.
        await FinishOrdersAsync(totals, cancellationToken);
    }

    private async Task FinishOrdersAsync(Totals totals, CancellationToken cancellationToken)
    {
        var page = await GetAsync<OrderPageView>(
            supervisor, "/api/outbound/orders?status=Released&pageSize=50", cancellationToken);

        foreach (var order in page?.Items ?? [])
        {
            var packed = await PostAsync<JsonElement?>(
                supervisor, $"/api/outbound/orders/{order.Id}/pack", new { }, cancellationToken);

            if (packed is null) continue;

            var despatched = await PostAsync<JsonElement?>(
                supervisor, $"/api/outbound/orders/{order.Id}/despatch", new { }, cancellationToken);

            if (despatched is not null) totals.Despatches++;
        }
    }

    /// <summary>
    /// Raises the shift's counts and does them.
    /// <para>
    /// The counts matter to a replay more than anything else the simulator produces.
    /// They are append-only, timestamped, and the only record in the system of
    /// whether the numbers it publishes are true — which makes them the natural
    /// ground truth for asking whether a change to the counting policy would have
    /// found more of what was wrong.
    /// </para>
    /// </summary>
    private async Task CountAsync(
        SimulationPlan plan,
        Random random,
        int day,
        Totals totals,
        CancellationToken cancellationToken)
    {
        var created = await PostAsync<CountPlanView>(
            supervisor,
            "/api/counting/plans",
            new { warehouseCode = plan.WarehouseCode, maxTasks = plan.CountsPerDay, notes = $"Simulated day {day}" },
            cancellationToken);

        if (created is null || created.Lines.Count == 0)
        {
            return;
        }

        totals.CountPlans++;

        foreach (var line in created.Lines.Where(l => l.WorkTaskId is not null))
        {
            var claimed = await PostAsync<TaskDetailView>(
                operatorClient, $"/api/work/tasks/{line.WorkTaskId}/claim", new { }, cancellationToken);

            if (claimed?.Task is not { } task) continue;

            // Mostly the book figure, sometimes not. A warehouse whose counts always
            // agreed would report perfect accuracy and prove nothing; one whose
            // counts never agreed would be a warehouse nobody could run.
            var variance = random.NextDouble() < plan.CountVarianceRate
                ? random.Next(-4, 5)
                : 0;

            var found = Math.Max(0m, task.Quantity + variance);

            var confirmed = await PostAsync<JsonElement?>(
                operatorClient,
                $"/api/work/tasks/{task.Id}/complete",
                new { toLocationCode = task.FromLocationCode, countedQuantity = found },
                cancellationToken);

            if (confirmed is null) continue;

            totals.Counts++;
            if (found != task.Quantity) totals.Variances++;
        }
    }

    // ----------------------------------------------------------- the fixtures

    /// <summary>
    /// Writes a counting policy for this warehouse with no quiet period.
    /// <para>
    /// The product's own answer to a problem the product created, used as intended:
    /// <c>count.settled_stock</c> is configuration on a rule, the rule set is data,
    /// and changing it is a row rather than a deploy. The set names this warehouse,
    /// so the shipped default — which is right, and which no real warehouse should
    /// run without — is left exactly where it is for everybody else.
    /// </para>
    /// </summary>
    private async Task OpenTheQuietPeriodAsync(string warehouseCode, CancellationToken cancellationToken)
    {
        var warehouses = await GetAsync<List<WarehouseView>>(supervisor, "/api/warehouses", cancellationToken);

        var warehouse = warehouses?.FirstOrDefault(w =>
            string.Equals(w.Code, warehouseCode, StringComparison.OrdinalIgnoreCase));

        if (warehouse is null)
        {
            Note($"warehouse {warehouseCode} not found; counting left on the shipped quiet period");
            return;
        }

        var sets = await GetAsync<RuleSetsView>(supervisor, "/api/rules/sets?kind=CycleCount", cancellationToken);

        var active = sets?.Items.FirstOrDefault(set => set.Status == "Active" && set.WarehouseId is null);

        if (active is null)
        {
            Note("no shared cycle-count rule set to clone; counting left as it is");
            return;
        }

        var rules = active.Rules
            .Select(rule => new
            {
                code = rule.Code,
                order = rule.Order,
                weight = rule.Weight,
                parameters = rule.Code == "count.settled_stock" ? "{\"quietPeriodHours\":0}" : rule.Parameters,
                isEnabled = rule.IsEnabled
            })
            .ToList();

        var draft = await PostAsync<RuleSetView>(
            supervisor,
            "/api/rules/sets",
            new
            {
                kind = "CycleCount",
                name = $"Simulated cycle count ({warehouseCode})",
                warehouseId = warehouse.Id,
                notes = "Written by the simulator. Quiet period removed so a warehouse built minutes ago is countable.",
                cloneFromRuleSetId = active.Id,
                rules
            },
            cancellationToken);

        if (draft is null)
        {
            Note("could not draft a warehouse-specific counting policy; counting left as it is");
            return;
        }

        var promoted = await PostAsync<RuleSetView>(
            supervisor, $"/api/rules/sets/{draft.Id}/promote", new { }, cancellationToken);

        if (promoted is null)
        {
            Note("drafted a counting policy but could not promote it; counting left as it is");
            return;
        }

        Report($"promoted '{promoted.Name}' v{promoted.Version} for {warehouseCode} with no quiet period");
    }

    private async Task<string?> FindDockAsync(CancellationToken cancellationToken)
    {
        var page = await GetAsync<LocationPageView>(
            supervisor, "/api/locations?type=ReceivingDock&pageSize=10", cancellationToken);

        return page?.Items.FirstOrDefault()?.Code;
    }

    private async Task<string?> FindBenchAsync(CancellationToken cancellationToken)
    {
        var page = await GetAsync<LocationPageView>(
            supervisor, "/api/locations?type=Packing&pageSize=10", cancellationToken);

        return page?.Items.FirstOrDefault()?.Code;
    }

    private async Task<string?> FindOtherStorageAsync(
        string notThisOne,
        Random random,
        CancellationToken cancellationToken)
    {
        _storage ??= (await GetAsync<LocationPageView>(
            supervisor, "/api/locations?type=Bulk&pageSize=500", cancellationToken))
            ?.Items.Select(location => location.Code).ToList() ?? [];

        if (_storage.Count == 0) return null;

        var pick = _storage[random.Next(_storage.Count)];

        return pick == notThisOne ? null : pick;
    }

    private List<string>? _storage;

    private async Task<IReadOnlyList<string>> FindSkusAsync(int count, CancellationToken cancellationToken)
    {
        var page = await GetAsync<ItemPageView>(
            supervisor,
            $"/api/items?pageSize={Math.Clamp(count, 1, 200).ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

        // Ambient only. A chilled item received onto an ambient dock and then
        // refused by every storage location is a real scenario and a good test of the
        // trace, but a warehouse made mostly of them puts almost nothing away, and
        // stock on shelves is what the whole exercise is for.
        return [.. (page?.Items ?? []).Where(item => item.TemperatureClass == "Ambient").Select(item => item.Sku)];
    }

    // ------------------------------------------------------------- the plumbing

    private async Task<T?> PostAsync<T>(
        HttpClient client,
        string route,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body, options: Json)
        };

        // A fresh key per call. The simulator is not testing idempotency here — the
        // loops do that implicitly whenever a retry happens — and reusing one would
        // make every receipt after the first a replay of the first.
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            Note($"POST {route} → {(int)response.StatusCode} {await ProblemAsync(response, cancellationToken)}");
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(HttpClient client, string route, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(route, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            Note($"GET {route} → {(int)response.StatusCode}");
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);

            return problem.TryGetProperty("detail", out var detail)
                ? detail.GetString() ?? string.Empty
                : problem.ToString();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Kept, capped, and never thrown. A refusal is information — a full aisle, a
    /// short pick, an order that cannot be packed — and a simulator that stopped at
    /// the first one would mostly be exercising the API's error handling.
    /// </summary>
    private void Note(string note)
    {
        if (_notes.Count < 200)
        {
            _notes.Add(note);
        }
    }

    private void Report(string message) => log?.Invoke(message);

    private sealed class Totals
    {
        public int Receipts;
        public int Putaways;
        public int Overrides;
        public int Orders;
        public int Short;
        public int Picks;
        public int Despatches;
        public int CountPlans;
        public int Counts;
        public int Variances;
    }

    // Only the fields the simulator reads. Deliberately not the API's own response
    // records: this project references nothing, so it sees the endpoints exactly as
    // an outside integrator would, and a field quietly renamed on the server shows
    // up here as a null rather than as a compile error nobody outside the solution
    // would have got either.
    private sealed record WarehouseView(Guid Id, string Code);

    private sealed record LocationView(string Code);

    private sealed record LocationPageView(List<LocationView> Items);

    private sealed record ItemView(string Sku, string TemperatureClass);

    private sealed record ItemPageView(List<ItemView> Items);

    private sealed record AsnLineView(int LineNumber, string Sku, decimal ExpectedQuantity, string? ExpectedLotNumber,
        DateOnly? ExpectedExpiryDate);

    private sealed record AsnView(Guid Id, string Reference, List<AsnLineView> Lines);

    private sealed record TaskView(
        Guid Id,
        string Sku,
        decimal Quantity,
        string FromLocationCode,
        string? SuggestedLocationCode);

    private sealed record TaskDetailView(TaskView? Task);

    private sealed record OrderView(Guid Id, string Reference);

    private sealed record OrderPageView(List<OrderView> Items);

    private sealed record ReleaseView(int TasksRaised, decimal ShortfallQuantity);

    private sealed record CountPlanLineView(Guid? WorkTaskId, string LocationCode);

    private sealed record CountPlanView(Guid Id, string Reference, List<CountPlanLineView> Lines);

    private sealed record RuleView(string Code, int Order, decimal Weight, string? Parameters, bool IsEnabled);

    private sealed record RuleSetView(
        Guid Id,
        string Name,
        int Version,
        string Status,
        Guid? WarehouseId,
        List<RuleView> Rules);

    private sealed record RuleSetsView(List<RuleSetView> Items);
}
