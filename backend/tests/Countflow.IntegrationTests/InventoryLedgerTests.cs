using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Countflow.IntegrationTests;

/// <summary>
/// The Phase 2 invariants, against a real PostgreSQL instance.
/// <para>
/// Deliberately the heaviest test class in the repository. Everything downstream —
/// allocation, picking, counting, shadow replay — reads the numbers this file
/// guards, and a ledger bug found in Phase 5 is a data-migration project rather
/// than a fix. The properties proved here are the ones ADR 0001 claims:
/// conservation, no over-allocation under concurrency, exact replay, idempotent
/// writes, and reconstructable history.
/// </para>
/// <para>
/// Each test works with its own freshly created item. Balances are keyed by item,
/// so tests cannot interfere with each other's arithmetic while still sharing one
/// container and one warehouse layout.
/// </para>
/// </summary>
public sealed class InventoryLedgerTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string SupervisorSecret = "Countflow#2026";

    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    // Created once and never disposed here: the factory owns them, and a response
    // whose HttpClient has already gone away is a class of flake worth designing
    // out rather than debugging.
    private HttpClient _supervisor = null!;
    private HttpClient _operator = null!;
    private string _supplierCode = string.Empty;
    private string _adjustmentCode = string.Empty;
    private List<string> _bins = [];
    private int _itemCounter;

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

            _supplierCode = await CodeOfAsync(masterData, LocationType.Supplier);
            _adjustmentCode = await CodeOfAsync(masterData, LocationType.Adjustment);

            _bins = await masterData.Locations
                .Where(l => l.Type == LocationType.PickFace || l.Type == LocationType.Bulk)
                .OrderBy(l => l.Code)
                .Select(l => l.Code)
                .Take(40)
                .ToListAsync();

            await EnsureUsersAsync(scope.ServiceProvider);
        }

        _supervisor = await SignInAsync("ledger-supervisor");
        _operator = await SignInAsync("ledger-operator");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------- receipts

    [Fact]
    public async Task Receiving_takes_stock_off_the_supplier_account_rather_than_creating_it()
    {
        var sku = await NewItemAsync();
        var bin = _bins[0];

        var receipt = await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 120m,
            toLocationCode = bin
        });

        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        var posted = await Read<ReceiptBody>(receipt);

        Assert.Equal(120m, posted.ToQuantityAfter);

        // The whole of double entry in one assertion: the goods did not appear, they
        // came from the supplier's account, which is now short exactly that much.
        Assert.Equal(-120m, posted.FromQuantityAfter);
        Assert.Equal(_supplierCode, posted.FromLocationCode);
        Assert.False(posted.WasAlreadyPosted);

        var onHand = await BalanceAsync(sku, bin);
        Assert.Equal(120m, onHand);
    }

    [Fact]
    public async Task A_lot_tracked_item_cannot_be_received_without_a_lot()
    {
        var sku = await NewItemAsync(lotTracked: true);

        var response = await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 10m,
            toLocationCode = _bins[1]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("inventory.lot_required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_receipt_carries_its_lot_expiry_and_licence_plate_through_to_the_balance()
    {
        var sku = await NewItemAsync(lotTracked: true, expiryTracked: true);
        var bin = _bins[2];
        var sscc = Sscc.FromPayload("30512345000000001");

        var response = await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 48m,
            toLocationCode = bin,
            lotNumber = "LOT-2211",
            expiryDate = "2027-03-31",
            sscc
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Filtered to the bin: an unfiltered enquiry also returns the supplier
        // contra row, which carries the same SKU and batch and is equally real.
        var page = await GetAsync<BalancePage>(
            $"/api/inventory/balances?sku={sku}&location={Uri.EscapeDataString(bin)}");
        var row = Assert.Single(page.Items);

        Assert.Equal("LOT-2211", row.LotNumber);
        Assert.Equal(new DateOnly(2027, 3, 31), row.ExpiryDate);
        Assert.Equal(sscc, row.Sscc);
        Assert.Equal(bin, row.LocationCode);
        Assert.Equal(48m, row.Quantity);
        Assert.True(row.IsAllocatable);
    }

    [Fact]
    public async Task The_same_batch_cannot_be_recorded_with_two_expiry_dates()
    {
        var sku = await NewItemAsync(lotTracked: true, expiryTracked: true);

        var first = await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 10m,
            toLocationCode = _bins[3],
            lotNumber = "LOT-CLASH",
            expiryDate = "2027-01-01"
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 10m,
            toLocationCode = _bins[3],
            lotNumber = "LOT-CLASH",
            expiryDate = "2027-06-30"
        });

        // Accepting this would silently change the rotation order of stock already
        // on the shelf.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("inventory.lot_expiry_conflict", await second.Content.ReadAsStringAsync());
    }

    // ----------------------------------------------------------- idempotency

    [Fact]
    public async Task A_retried_command_posts_once_and_reports_the_original_entry()
    {
        var sku = await NewItemAsync();
        var bin = _bins[4];
        var key = $"retry-{Guid.CreateVersion7()}";

        var body = new { sku, quantity = 60m, toLocationCode = bin };

        var first = await PostAsync("/api/inventory/receipts", body, key);
        var second = await PostAsync("/api/inventory/receipts", body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var one = await Read<ReceiptBody>(first);
        var two = await Read<ReceiptBody>(second);

        // The retry is a success, not an error — that is the entire point of the
        // key. It just did not write anything.
        Assert.False(one.WasAlreadyPosted);
        Assert.True(two.WasAlreadyPosted);
        Assert.Equal(one.Sequence, two.Sequence);
        Assert.Equal(one.MovementId, two.MovementId);

        Assert.Equal(60m, await BalanceAsync(sku, bin));
        Assert.Equal(1, await MovementCountAsync(sku));
    }

    [Fact]
    public async Task Concurrent_copies_of_one_retried_command_still_post_exactly_once()
    {
        var sku = await NewItemAsync();
        var bin = _bins[5];
        var key = $"race-{Guid.CreateVersion7()}";

        var body = new { sku, quantity = 25m, toLocationCode = bin };

        // Eight copies at once, past the cheap pre-check and into the unique index.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => PostAsync("/api/inventory/receipts", body, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        Assert.Equal(25m, await BalanceAsync(sku, bin));
        Assert.Equal(1, await MovementCountAsync(sku));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_movement_is_refused_rather_than_replayed()
    {
        var sku = await NewItemAsync();
        var key = $"reused-{Guid.CreateVersion7()}";

        var first = await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 10m,
            toLocationCode = _bins[6]
        }, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 99m,
            toLocationCode = _bins[6]
        }, key);

        // Returning the first receipt here would report success for a movement that
        // never happened — the one failure mode worse than an error.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("inventory.idempotency_key_reused", await second.Content.ReadAsStringAsync());
        Assert.Equal(10m, await BalanceAsync(sku, _bins[6]));
    }

    [Fact]
    public async Task A_command_without_an_idempotency_key_is_refused()
    {
        var sku = await NewItemAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/receipts")
        {
            Content = JsonContent.Create(new { sku, quantity = 5m, toLocationCode = _bins[7] })
        };

        var response = await _supervisor.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("inventory.idempotency_key_required", await response.Content.ReadAsStringAsync());
    }

    // -------------------------------------------------------------- movements

    [Fact]
    public async Task A_transfer_moves_stock_without_changing_how_much_there_is()
    {
        var sku = await NewItemAsync();
        var (source, destination) = (_bins[8], _bins[9]);

        await ReceiveAsync(sku, 100m, source);

        var response = await PostAsync("/api/inventory/transfers", new
        {
            sku,
            quantity = 40m,
            fromLocationCode = source,
            toLocationCode = destination
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = await Read<ReceiptBody>(response);

        Assert.Equal(60m, moved.FromQuantityAfter);
        Assert.Equal(40m, moved.ToQuantityAfter);
        Assert.Equal(100m, await BalanceAsync(sku, source) + await BalanceAsync(sku, destination));
    }

    [Fact]
    public async Task A_shelf_cannot_be_driven_negative()
    {
        var sku = await NewItemAsync();
        var (source, destination) = (_bins[10], _bins[11]);

        await ReceiveAsync(sku, 10m, source);

        var response = await PostAsync("/api/inventory/transfers", new
        {
            sku,
            quantity = 11m,
            fromLocationCode = source,
            toLocationCode = destination
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("inventory.insufficient_stock", body);

        // The error names what is actually there, which is the difference between an
        // operator fixing the count and an operator filing a ticket.
        Assert.Contains("10", body);

        Assert.Equal(10m, await BalanceAsync(sku, source));
        Assert.Equal(0m, await BalanceAsync(sku, destination));
    }

    [Fact]
    public async Task Quarantining_stock_is_a_ledger_entry_rather_than_a_flag()
    {
        var sku = await NewItemAsync();
        var bin = _bins[12];

        await ReceiveAsync(sku, 30m, bin);

        var response = await PostAsync("/api/inventory/status-changes", new
        {
            sku,
            quantity = 12m,
            locationCode = bin,
            fromStatus = "Available",
            toStatus = "Quarantine",
            reason = "DamageFound",
            notes = "Crushed corner on the top layer"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await GetAsync<BalancePage>(
            $"/api/inventory/balances?sku={sku}&location={Uri.EscapeDataString(bin)}");

        var available = page.Items.Single(r => r.Status == "Available");
        var quarantined = page.Items.Single(r => r.Status == "Quarantine");

        Assert.Equal(18m, available.Quantity);
        Assert.Equal(12m, quarantined.Quantity);
        Assert.True(available.IsAllocatable);
        Assert.False(quarantined.IsAllocatable);

        // "Who quarantined this, when, and why" is the question asked after a
        // recall. A status column overwritten in place cannot answer it.
        var ledger = await GetAsync<MovementPage>($"/api/inventory/movements?sku={sku}");
        var entry = ledger.Items.Single(m => m.Type == "StatusChange");

        Assert.Equal("DamageFound", entry.Reason);
        Assert.Equal("Crushed corner on the top layer", entry.Notes);
        Assert.Equal("ledger-supervisor", entry.UserName);
        Assert.Equal(bin, entry.FromLocationCode);
        Assert.Equal(bin, entry.ToLocationCode);
    }

    [Fact]
    public async Task A_status_change_that_changes_nothing_is_refused()
    {
        var sku = await NewItemAsync();
        await ReceiveAsync(sku, 5m, _bins[13]);

        var response = await PostAsync("/api/inventory/status-changes", new
        {
            sku,
            quantity = 5m,
            locationCode = _bins[13],
            fromStatus = "Available",
            toStatus = "Available",
            reason = "QualityHoldReleased"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_movement_type_cannot_use_the_wrong_counterparty()
    {
        var sku = await NewItemAsync();
        await ReceiveAsync(sku, 5m, _bins[14]);

        // Two physical locations, labelled a receipt. It balances arithmetically and
        // would make every inbound report a lie.
        var response = await PostAsync("/api/inventory/transfers", new
        {
            sku,
            quantity = 5m,
            fromLocationCode = _bins[14],
            toLocationCode = _bins[15],
            type = "Receipt"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("inventory.invalid_counterparty", await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------ corrections

    [Fact]
    public async Task A_reversal_restores_the_balance_and_leaves_the_original_entry_intact()
    {
        var sku = await NewItemAsync();
        var bin = _bins[16];

        var receipt = await Read<ReceiptBody>(await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 75m,
            toLocationCode = bin
        }));

        var reversal = await PostAsync($"/api/inventory/movements/{receipt.MovementId}/reversal", new
        {
            notes = "Booked against the wrong purchase order"
        });

        Assert.Equal(HttpStatusCode.OK, reversal.StatusCode);

        Assert.Equal(0m, await BalanceAsync(sku, bin));

        var ledger = await GetAsync<MovementPage>($"/api/inventory/movements?sku={sku}");

        // Two entries, not zero. The correction adds to the record rather than
        // erasing it — an auditor can see that something was posted in error, who
        // undid it, and when.
        Assert.Equal(2, ledger.Items.Count);

        var original = ledger.Items.Single(m => m.Id == receipt.MovementId);
        Assert.Equal(75m, original.Quantity);
        Assert.True(original.IsReversed);

        var compensating = ledger.Items.Single(m => m.Type == "Reversal");
        Assert.Equal(receipt.MovementId, compensating.ReversesMovementId);
        Assert.Equal(bin, compensating.FromLocationCode);
        Assert.Equal(_supplierCode, compensating.ToLocationCode);
    }

    [Fact]
    public async Task A_movement_can_only_be_reversed_once()
    {
        var sku = await NewItemAsync();

        var receipt = await Read<ReceiptBody>(await PostAsync("/api/inventory/receipts", new
        {
            sku,
            quantity = 20m,
            toLocationCode = _bins[17]
        }));

        var url = $"/api/inventory/movements/{receipt.MovementId}/reversal";

        // Distinct keys, so this tests the "already reversed" rule rather than
        // idempotent replay.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(url, new { }, $"rev-a-{sku}")).StatusCode);

        var second = await PostAsync(url, new { }, $"rev-b-{sku}");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("inventory.already_reversed", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_adjustment_records_a_gain_or_a_loss_against_the_adjustment_account()
    {
        var sku = await NewItemAsync();
        var bin = _bins[18];

        await ReceiveAsync(sku, 50m, bin);

        var loss = await PostAsync("/api/inventory/adjustments", new
        {
            sku,
            quantity = -8m,
            locationCode = bin,
            reason = "CycleCountVariance",
            notes = "Counted 42, system said 50"
        });

        Assert.Equal(HttpStatusCode.OK, loss.StatusCode);
        Assert.Equal(42m, await BalanceAsync(sku, bin));

        var entry = (await GetAsync<MovementPage>($"/api/inventory/movements?sku={sku}"))
            .Items.Single(m => m.Type == "CountAdjustment");

        Assert.Equal(bin, entry.FromLocationCode);
        Assert.Equal(_adjustmentCode, entry.ToLocationCode);
        Assert.Equal(8m, entry.Quantity);

        var gain = await PostAsync("/api/inventory/adjustments", new
        {
            sku,
            quantity = 3m,
            locationCode = bin,
            reason = "StocktakeVariance"
        });

        Assert.Equal(HttpStatusCode.OK, gain.StatusCode);
        Assert.Equal(45m, await BalanceAsync(sku, bin));
    }

    [Fact]
    public async Task An_operator_cannot_adjust_stock()
    {
        var sku = await NewItemAsync();
        await ReceiveAsync(sku, 10m, _bins[19]);

        var response = await PostAsync("/api/inventory/adjustments", new
        {
            sku,
            quantity = -10m,
            locationCode = _bins[19],
            reason = "ManualCorrection"
        }, client: _operator);

        // Executing directed work and deciding the system is wrong are different
        // authorities. An adjustment endpoint open to every handheld is how
        // shrinkage gets papered over instead of investigated.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(10m, await BalanceAsync(sku, _bins[19]));
    }

    [Fact]
    public async Task An_operator_can_still_move_stock()
    {
        var sku = await NewItemAsync();
        await ReceiveAsync(sku, 10m, _bins[20]);

        var response = await PostAsync("/api/inventory/transfers", new
        {
            sku,
            quantity = 10m,
            fromLocationCode = _bins[20],
            toLocationCode = _bins[21],
            type = "Putaway",
            reason = "PutawayComplete"
        }, client: _operator);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------- conservation and replay

    [Fact]
    public async Task Stock_is_never_created_or_destroyed_across_a_randomised_sequence()
    {
        // Fixed seed: a property test that cannot be reproduced is a rumour.
        var random = new Random(20260811);

        var skus = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            skus.Add(await NewItemAsync());
        }

        var bins = _bins.Skip(22).Take(6).ToList();
        var placed = new Dictionary<(string Sku, string Bin), decimal>();

        decimal Held(string sku, string bin) => placed.GetValueOrDefault((sku, bin), 0m);

        for (var step = 0; step < 120; step++)
        {
            var sku = skus[random.Next(skus.Count)];
            var bin = bins[random.Next(bins.Count)];

            switch (random.Next(4))
            {
                case 0:
                {
                    var quantity = random.Next(1, 40);
                    var response = await ReceiveAsync(sku, quantity, bin);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    placed[(sku, bin)] = Held(sku, bin) + quantity;
                    break;
                }

                case 1:
                {
                    var target = bins[random.Next(bins.Count)];
                    var available = Held(sku, bin);
                    if (target == bin || available <= 0) continue;

                    var quantity = Math.Min(available, random.Next(1, 20));

                    var response = await PostAsync("/api/inventory/transfers", new
                    {
                        sku,
                        quantity,
                        fromLocationCode = bin,
                        toLocationCode = target
                    });
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    placed[(sku, bin)] = available - quantity;
                    placed[(sku, target)] = Held(sku, target) + quantity;
                    break;
                }

                case 2:
                {
                    var available = Held(sku, bin);
                    if (available <= 0) continue;

                    var quantity = Math.Min(available, random.Next(1, 10));

                    var response = await PostAsync("/api/inventory/adjustments", new
                    {
                        sku,
                        quantity = -quantity,
                        locationCode = bin,
                        reason = "CycleCountVariance"
                    });
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    placed[(sku, bin)] = available - quantity;
                    break;
                }

                default:
                {
                    var available = Held(sku, bin);
                    if (available <= 0) continue;

                    var quantity = Math.Min(available, random.Next(1, 10));

                    // Quarantine leaves the location total alone, which is exactly
                    // why it belongs in a conservation test: a status change that
                    // leaked quantity would look like a rounding error.
                    var response = await PostAsync("/api/inventory/status-changes", new
                    {
                        sku,
                        quantity,
                        locationCode = bin,
                        fromStatus = "Available",
                        toStatus = "Quarantine",
                        reason = "QualityHoldApplied"
                    });
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    placed[(sku, bin)] = available - quantity;
                    break;
                }
            }
        }

        // 1. The ledger balances. Every entry names both sides, so the signed sum of
        //    everything ever posted is zero — this is conservation as a property of
        //    the schema rather than of the code that wrote it.
        var report = await ReconcileAsync();
        Assert.Equal(0m, report.LedgerSum);

        // 2. The projection agrees with the ledger, holding for holding.
        Assert.Empty(report.Drift);
        Assert.True(report.IsClean);

        // 3. And the balances match what the test itself counted, independently of
        //    both. Available stock only: the quarantined portion is still in the bin
        //    but is a different holding.
        foreach (var ((sku, bin), expected) in placed)
        {
            Assert.Equal(expected, await BalanceAsync(sku, bin));
        }
    }

    [Fact]
    public async Task Concurrent_movements_out_of_one_balance_never_over_allocate()
    {
        var sku = await NewItemAsync();
        var (source, destination) = (_bins[28], _bins[29]);

        await ReceiveAsync(sku, 100m, source);

        // Twenty simultaneous attempts to take 10 from a bin holding 100. Exactly
        // ten can succeed. Without the row lock taken before the sufficiency check,
        // several would read 100, all decide there is enough, and the bin would go
        // negative — the classic double-allocation that puts a picker in front of an
        // empty shelf.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            PostAsync("/api/inventory/transfers", new
            {
                sku,
                quantity = 10m,
                fromLocationCode = source,
                toLocationCode = destination
            }, $"concurrent-{sku}-{i}")));

        var succeeded = attempts.Count(r => r.StatusCode == HttpStatusCode.OK);
        var refused = attempts.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(20, succeeded + refused);
        Assert.Equal(10, succeeded);

        Assert.Equal(0m, await BalanceAsync(sku, source));
        Assert.Equal(100m, await BalanceAsync(sku, destination));

        var report = await ReconcileAsync();
        Assert.True(report.IsClean);
    }

    [Fact]
    public async Task Movements_in_opposite_directions_do_not_deadlock()
    {
        var sku = await NewItemAsync();
        var (left, right) = (_bins[30], _bins[31]);

        await ReceiveAsync(sku, 200m, left);
        await ReceiveAsync(sku, 200m, right);

        // The deadlock shape: A→B and B→A at the same instant, each transaction
        // wanting the row the other holds. Both writers sort the two balance rows by
        // the same key before locking, so the acquisition order is global and no
        // cycle can form.
        var crossfire = Enumerable.Range(0, 12).Select(i =>
        {
            var forwards = i % 2 == 0;

            return PostAsync("/api/inventory/transfers", new
            {
                sku,
                quantity = 5m,
                fromLocationCode = forwards ? left : right,
                toLocationCode = forwards ? right : left
            }, $"deadlock-{sku}-{i}");
        });

        var responses = await Task.WhenAll(crossfire);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(400m, await BalanceAsync(sku, left) + await BalanceAsync(sku, right));
    }

    [Fact]
    public async Task A_replay_of_the_ledger_reproduces_every_stored_balance()
    {
        var sku = await NewItemAsync();
        var bin = _bins[32];

        await ReceiveAsync(sku, 90m, bin);
        await PostAsync("/api/inventory/transfers", new
        {
            sku,
            quantity = 30m,
            fromLocationCode = bin,
            toLocationCode = _bins[33]
        });

        var report = await ReconcileAsync();

        Assert.True(report.IsClean);
        Assert.Equal(0m, report.LedgerSum);
        Assert.True(report.HoldingsChecked > 0);
        Assert.True(report.MovementsReplayed > 0);
    }

    [Fact]
    public async Task Reconciliation_reports_drift_when_a_balance_is_tampered_with()
    {
        var sku = await NewItemAsync();
        var bin = _bins[34];

        await ReceiveAsync(sku, 64m, bin);

        // Balances are a projection, so they *can* be written to — which is exactly
        // why the reconciler exists. This forges the drift the alarm is built to
        // catch, and proves the alarm is not simply always silent.
        await ExecuteAsync(
            """
            UPDATE inventory."StockBalances" SET "Quantity" = "Quantity" + 5
            WHERE "ItemId" = (SELECT "Id" FROM masterdata."Items" WHERE "Sku" = @sku)
              AND "AllowsNegative" = false
            """,
            ("sku", sku));

        var report = await ReconcileAsync();

        Assert.False(report.IsClean);
        var drift = Assert.Single(report.Drift, d => d.Sku == sku);
        Assert.Equal(69m, drift.Projected);
        Assert.Equal(64m, drift.Replayed);
        Assert.Equal(5m, drift.Difference);

        // The ledger itself is untouched: the tampering moved the projection, not
        // the truth.
        Assert.Equal(0m, report.LedgerSum);

        // Put it back, so the shared container's later assertions still hold.
        await ExecuteAsync(
            """
            UPDATE inventory."StockBalances" SET "Quantity" = "Quantity" - 5
            WHERE "ItemId" = (SELECT "Id" FROM masterdata."Items" WHERE "Sku" = @sku)
              AND "AllowsNegative" = false
            """,
            ("sku", sku));

        Assert.True((await ReconcileAsync()).IsClean);
    }

    // ------------------------------------------------------- point in time

    [Fact]
    public async Task The_contents_of_a_bin_can_be_reconstructed_as_of_a_past_instant()
    {
        var sku = await NewItemAsync();
        var bin = _bins[35];

        await ReceiveAsync(sku, 100m, bin);
        await ReceiveAsync(sku, 50m, bin);

        var snapshot = await BalanceAsync(sku, bin);
        Assert.Equal(150m, snapshot);

        // The timestamp is server-assigned, so the cutoff has to come from the
        // ledger rather than from the test's own clock.
        var asOf = (await GetAsync<MovementPage>($"/api/inventory/movements?sku={sku}"))
            .Items.Max(m => m.OccurredAt);

        await Task.Delay(20);

        await PostAsync("/api/inventory/transfers", new
        {
            sku,
            quantity = 120m,
            fromLocationCode = bin,
            toLocationCode = _bins[36]
        });

        await PostAsync("/api/inventory/adjustments", new
        {
            sku,
            quantity = -10m,
            locationCode = bin,
            reason = "CycleCountVariance"
        });

        Assert.Equal(20m, await BalanceAsync(sku, bin));

        var historic = await GetAsync<BalancePage>(
            $"/api/inventory/balances?sku={sku}&location={Uri.EscapeDataString(bin)}" +
            $"&asOf={Uri.EscapeDataString(asOf.ToString("O"))}");

        // "Show me A-07-03-02 as of 14 July" — the query a mutable quantity column
        // can never answer, and the reason the ledger is immutable.
        var row = Assert.Single(historic.Items);
        Assert.Equal(snapshot, row.Quantity);
        Assert.NotNull(historic.AsOfSequence);
    }

    [Fact]
    public async Task A_point_in_time_enquiry_before_any_movement_is_empty_rather_than_an_error()
    {
        var sku = await NewItemAsync();
        await ReceiveAsync(sku, 10m, _bins[37]);

        var page = await GetAsync<BalancePage>(
            $"/api/inventory/balances?sku={sku}&asOf=2020-01-01T00%3A00%3A00Z");

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task A_point_in_time_enquiry_in_the_future_is_refused()
    {
        var asOf = DateTimeOffset.UtcNow.AddDays(1).ToString("O");

        var response = await _supervisor.GetAsync(
            $"/api/inventory/balances?asOf={Uri.EscapeDataString(asOf)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("inventory.as_of_in_future", await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------- the append-only rule

    [Fact]
    public async Task The_database_refuses_to_update_a_ledger_row()
    {
        var sku = await NewItemAsync();
        await ReceiveAsync(sku, 15m, _bins[38]);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """UPDATE inventory."StockMovements" SET "Quantity" = 999 WHERE "Quantity" = 15"""));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
        Assert.Contains("append-only", exception.MessageText);
    }

    [Fact]
    public async Task The_database_refuses_to_delete_a_ledger_row_even_when_it_matches_nothing()
    {
        // A zero-row UPDATE or DELETE that reports success is how someone concludes
        // the table is editable and writes the code that assumes it. The trigger is
        // statement-level so that this fails too.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """DELETE FROM inventory."StockMovements" WHERE "Quantity" = -1"""));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
    }

    [Fact]
    public async Task The_database_refuses_to_truncate_the_ledger()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "TRUNCATE inventory.\"StockMovements\""));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
    }

    [Fact]
    public async Task The_context_refuses_to_persist_a_modified_ledger_row()
    {
        var sku = await NewItemAsync();
        await ReceiveAsync(sku, 7m, _bins[39]);

        using var scope = fixture.Services.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var movement = await inventory.StockMovements.FirstAsync(m => m.Quantity == 7m);

        // Every property is init-only, so this needs the change tracker to reach it —
        // which is precisely the door the guard closes, and it closes it with a
        // message that says what to do instead.
        inventory.Entry(movement).Property(m => m.Notes).CurrentValue = "tampered";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => inventory.SaveChangesAsync());

        Assert.Contains("append-only", exception.Message);
        Assert.Contains("compensating", exception.Message);
    }

    [Fact]
    public async Task The_database_refuses_a_negative_balance_on_a_physical_location()
    {
        var sku = await NewItemAsync();
        var bin = _bins[0];

        await ReceiveAsync(sku, 5m, bin);

        // The posting path already refuses this with a readable error. The check
        // constraint is the backstop for every code path that has not been written
        // yet — a future bulk import, a repair script, a hand-run UPDATE.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            UPDATE inventory."StockBalances" SET "Quantity" = -1
            WHERE "ItemId" = (SELECT "Id" FROM masterdata."Items" WHERE "Sku" = @sku)
              AND "AllowsNegative" = false
            """,
            ("sku", sku)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains("NonNegativeUnlessCounterparty", exception.ConstraintName ?? string.Empty);
    }

    [Fact]
    public async Task The_supplier_counterparty_is_allowed_to_go_negative()
    {
        var sku = await NewItemAsync();
        await ReceiveAsync(sku, 12m, _bins[1]);

        var page = await GetAsync<BalancePage>(
            $"/api/inventory/balances?sku={sku}&location={Uri.EscapeDataString(_supplierCode)}");

        // Not a bug to be fixed: it is the contra account. A warehouse that has
        // received anything owes its supplier account exactly that much, and this is
        // what makes one mechanism cover receipt, despatch and adjustment alike.
        var row = Assert.Single(page.Items);
        Assert.Equal(-12m, row.Quantity);
    }

    // --------------------------------------------------------------- helpers

    private async Task<string> NewItemAsync(
        bool lotTracked = false,
        bool expiryTracked = false,
        [CallerMemberName] string caller = "")
    {
        // A fresh item per test. Balances are keyed by item, so tests sharing one
        // container and one warehouse still cannot corrupt each other's arithmetic.
        var sku = $"LEDGER-{Interlocked.Increment(ref _itemCounter):D3}-{caller[..Math.Min(caller.Length, 24)]}";

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var item = new Item
        {
            Sku = sku,
            Description = $"Ledger test item for {caller}",
            BaseUom = "EA",
            IsLotTracked = lotTracked || expiryTracked,
            IsExpiryTracked = expiryTracked
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

    private Task<HttpResponseMessage> ReceiveAsync(string sku, decimal quantity, string locationCode) =>
        PostAsync("/api/inventory/receipts", new { sku, quantity, toLocationCode = locationCode });

    private async Task<decimal> BalanceAsync(string sku, string locationCode)
    {
        var page = await GetAsync<BalancePage>(
            $"/api/inventory/balances?sku={Uri.EscapeDataString(sku)}" +
            $"&location={Uri.EscapeDataString(locationCode)}&status=Available");

        return page.Items.Sum(r => r.Quantity);
    }

    private async Task<int> MovementCountAsync(string sku) =>
        (await GetAsync<MovementPage>($"/api/inventory/movements?sku={Uri.EscapeDataString(sku)}")).Total;

    private async Task<ReconciliationBody> ReconcileAsync()
    {
        var response = await _supervisor.PostAsync("/api/inventory/reconciliation", content: null);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ReconciliationBody>())!;
    }

    private async Task<HttpResponseMessage> PostAsync(
        string url,
        object body,
        string? idempotencyKey = null,
        HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.CreateVersion7().ToString());

        return await (client ?? _supervisor).SendAsync(request);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _supervisor.GetAsync(url);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>())!;

    /// <summary>
    /// Raw SQL, straight past the application. Used only to prove that the
    /// database-level guards hold when the application is not in the way, which is
    /// the whole point of having them.
    /// </summary>
    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var scope = fixture.Services.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var connection = new NpgsqlConnection(inventory.Database.GetConnectionString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static Task<string> CodeOfAsync(MasterDataDbContext dbContext, LocationType type) =>
        dbContext.Locations.Where(l => l.Type == type).Select(l => l.Code).FirstAsync();

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

        await EnsureAsync("ledger-supervisor", "LDG-SUP", Roles.Supervisor);
        await EnsureAsync("ledger-operator", "LDG-OP", Roles.Operator);

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

            var created = await users.CreateAsync(user, SupervisorSecret);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

            await users.AddToRoleAsync(user, role);
        }
    }

    private async Task<HttpClient> SignInAsync(string userName)
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            Credentials(userName, SupervisorSecret));

        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.AccessToken);

        return client;
    }

    private sealed record LoginResponse(string AccessToken);

    private sealed record ReceiptBody(
        Guid MovementId,
        long Sequence,
        DateTimeOffset OccurredAt,
        string Sku,
        decimal Quantity,
        string FromLocationCode,
        string ToLocationCode,
        decimal FromQuantityAfter,
        decimal ToQuantityAfter,
        bool WasAlreadyPosted);

    private sealed record BalancePage(
        List<BalanceRow> Items,
        int Total,
        DateTimeOffset? AsOf,
        long? AsOfSequence);

    private sealed record BalanceRow(
        string Sku,
        string? LotNumber,
        DateOnly? ExpiryDate,
        string LocationCode,
        string? Sscc,
        string Status,
        decimal Quantity,
        bool IsAllocatable);

    private sealed record MovementPage(List<MovementRow> Items, int Total);

    private sealed record MovementRow(
        Guid Id,
        long Sequence,
        DateTimeOffset OccurredAt,
        string Sku,
        string? LotNumber,
        decimal Quantity,
        string FromLocationCode,
        string ToLocationCode,
        string Type,
        string Reason,
        string UserName,
        Guid? ReversesMovementId,
        bool IsReversed,
        string? Notes);

    private sealed record ReconciliationBody(
        long MovementsReplayed,
        int HoldingsChecked,
        decimal LedgerSum,
        bool IsClean,
        List<DriftRow> Drift);

    private sealed record DriftRow(
        string Sku,
        string LocationCode,
        decimal Projected,
        decimal Replayed,
        decimal Difference);
}
