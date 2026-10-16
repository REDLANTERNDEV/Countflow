using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

public sealed class StrandedReservationTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>
{
    [Fact]
    public async Task Owner_lookup_includes_competing_claims_when_each_claim_alone_would_fit()
    {
        var owner = Guid.CreateVersion7();
        var balance = Holding(10m);
        var mine = Claim(balance, owner, 6m);
        var theirs = Claim(balance, Guid.CreateVersion7(), 6m);
        await SeedAsync(balance, mine, theirs);

        using var scope = fixture.Services.CreateScope();
        var reservations = scope.ServiceProvider.GetRequiredService<IStockReservations>();
        var row = Assert.Single(await reservations.FindStrandedByOwnerAsync(owner, CancellationToken.None));
        Assert.Equal(mine.Id, row.Reservation.Id);
        Assert.Equal(balance.Id, row.Reservation.BalanceId);
        Assert.Equal(balance.ItemId, row.Reservation.ItemId);
        Assert.Equal(balance.LocationId, row.Reservation.LocationId);
        Assert.Equal(owner, row.Reservation.OwnerId);
        Assert.Equal(6m, row.Reservation.Quantity);
        Assert.Equal(10m, row.OnHandQuantity);
        Assert.Equal(12m, row.TotalReserved);
        Assert.Equal(2m, row.Shortfall);
        var other = Assert.Single(await reservations.FindStrandedByOwnerAsync(theirs.OwnerId, CancellationToken.None));
        Assert.Equal(theirs.Id, other.Reservation.Id);
        Assert.Equal(2m, other.Shortfall);
        Assert.Empty(await reservations.FindStrandedByOwnerAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [Theory]
    [InlineData(ReservationState.Released)]
    [InlineData(ReservationState.Picked)]
    public async Task Closed_claims_are_absent_from_results_and_the_holding_total(ReservationState closedState)
    {
        var owner = Guid.CreateVersion7();
        var balance = Holding(5m);
        var open = Claim(balance, owner, 5m);
        var closed = Claim(balance, owner, 20m, closedState);
        await SeedAsync(balance, open, closed);

        using var scope = fixture.Services.CreateScope();
        var reservations = scope.ServiceProvider.GetRequiredService<IStockReservations>();
        Assert.Empty(await reservations.FindStrandedByOwnerAsync(owner, CancellationToken.None));
        // No underlying quantity or claim lifecycle changes from looking.
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(5m, (await db.StockBalances.SingleAsync(row => row.Id == balance.Id)).Quantity);
        Assert.Equal(closedState, (await db.StockReservations.SingleAsync(row => row.Id == closed.Id)).State);
    }

    [Fact]
    public async Task Only_overcommitted_holdings_are_returned_even_for_the_same_item_and_location()
    {
        var owner = Guid.CreateVersion7();
        var empty = Holding(0m);
        var exact = Holding(4m);
        // A holding's status is part of its identity; sharing SKU and bay is not
        // enough to combine stock or claims. Separate fixture data is deliberate.
        var covered = new StockBalance { ItemId = empty.ItemId, LocationId = empty.LocationId,
            Status = InventoryStatus.OnHold, Quantity = 8m, AllowsNegative = false };
        var stranded = Claim(empty, owner, 3m);
        await SeedAsync(empty, stranded);
        await SeedAsync(covered, Claim(covered, owner, 6m));
        await SeedAsync(exact, Claim(exact, owner, 4m));

        using var scope = fixture.Services.CreateScope();
        var reservations = scope.ServiceProvider.GetRequiredService<IStockReservations>();
        for (var retry = 0; retry < 2; retry++)
        {
            var row = Assert.Single(await reservations.FindStrandedByOwnerAsync(owner, CancellationToken.None));
            Assert.Equal(stranded.Id, row.Reservation.Id);
            Assert.Equal(0m, row.OnHandQuantity);
            Assert.Equal(3m, row.TotalReserved);
            Assert.Equal(3m, row.Shortfall);
        }
    }

    [Theory]
    [InlineData(ReservationState.Released)]
    [InlineData(ReservationState.Picked)]
    public async Task Closed_claims_do_not_reappear_alongside_an_open_stranded_claim(ReservationState closedState)
    {
        var owner = Guid.CreateVersion7();
        var balance = Holding(0m);
        var open = Claim(balance, owner, 2m);
        await SeedAsync(balance, open, Claim(balance, owner, 7m, closedState));

        using var scope = fixture.Services.CreateScope();
        var reservations = scope.ServiceProvider.GetRequiredService<IStockReservations>();
        var row = Assert.Single(await reservations.FindStrandedByOwnerAsync(owner, CancellationToken.None));
        Assert.Equal(open.Id, row.Reservation.Id);
        Assert.Equal(2m, row.TotalReserved);
        Assert.Equal(2m, row.Shortfall);

        var released = await reservations.ReleaseAsync([open.Id], "test resolved claim", CancellationToken.None);
        Assert.True(released.IsSuccess);
        Assert.Equal(1, released.Value);
        Assert.Empty(await reservations.FindStrandedByOwnerAsync(owner, CancellationToken.None));
    }

    private async Task SeedAsync(StockBalance balance, params StockReservation[] claims)
    {
        // This read-model test supplies diagnostic inputs directly in its isolated
        // database. Production stock still enters only through StockLedger.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        db.StockBalances.Add(balance);
        db.StockReservations.AddRange(claims);
        await db.SaveChangesAsync();
    }

    private static StockBalance Holding(decimal quantity) => new()
    {
        ItemId = Guid.CreateVersion7(), LocationId = Guid.CreateVersion7(),
        Status = InventoryStatus.Available, Quantity = quantity, AllowsNegative = false
    };

    private static StockReservation Claim(StockBalance holding, Guid owner, decimal quantity,
        ReservationState state = ReservationState.Open) => new()
    {
        BalanceId = holding.Id, OwnerId = owner, OwnerReference = $"SO-{owner:N}#1",
        Quantity = quantity, State = state, ClosedAt = state == ReservationState.Open ? null : DateTimeOffset.UtcNow, IdempotencyKey = Guid.CreateVersion7().ToString(),
        UserId = Guid.Empty, UserName = "stranded-lookup-test", CreatedAt = DateTimeOffset.UtcNow
    };
}
