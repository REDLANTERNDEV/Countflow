using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.Work.Domain;
using Countflow.Modules.Work.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;
public sealed class DashboardTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>
{
    [Fact]
    public async Task Throughput_uses_completion_time_and_backlog_includes_old_work()
    {
        var warehouseId = await WarehouseAsync(); var now = DateTimeOffset.UtcNow;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            WorkTask Task(WorkTaskStatus status, DateTimeOffset created, DateTimeOffset? completed) => new() {
                WarehouseId = warehouseId, Kind = WorkTaskKind.Putaway, ItemId = Guid.CreateVersion7(), FromLocationId = Guid.CreateVersion7(),
                Quantity = 1, Status = status, CreatedAt = created, CompletedAt = completed, CompletedAtLocationId = completed is null ? null : Guid.CreateVersion7() };
            db.WorkTasks.AddRange(Task(WorkTaskStatus.Completed, now.AddDays(-30), now.AddHours(-1)),
                Task(WorkTaskStatus.Completed, now.AddDays(-1), now.AddDays(-10)), Task(WorkTaskStatus.Ready, now.AddDays(-30), null), Task(WorkTaskStatus.Assigned, now.AddDays(-30), null));
            await db.SaveChangesAsync();
        }
        using var client = await ClientAsync();
        string connection;
        using (var scope = fixture.Services.CreateScope()) connection = scope.ServiceProvider.GetRequiredService<WorkDbContext>().Database.GetConnectionString()!;
        using var nonUtc = fixture.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Countflow", connection + ";Timezone=Pacific/Auckland"));
        using var observed = nonUtc.CreateClient(); observed.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        var response = await observed.GetAsync($"/api/v1/work/throughput?warehouseId={warehouseId}&days=7"); response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, data.GetProperty("completed").GetInt32()); Assert.Equal(1, data.GetProperty("ready").GetInt32()); Assert.Equal(1, data.GetProperty("assigned").GetInt32());
        Assert.Equal(7, data.GetProperty("days").GetArrayLength());
        Assert.Equal(DateOnly.FromDateTime(now.AddHours(-1).UtcDateTime).ToString("yyyy-MM-dd"), data.GetProperty("days").EnumerateArray().Single(day => day.GetProperty("completed").GetInt32() > 0).GetProperty("date").GetString());
        Assert.Equal(1, data.GetProperty("days").EnumerateArray().Sum(day => day.GetProperty("completed").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/work/throughput?warehouseId={warehouseId}&days=500")).StatusCode);
    }

    [Fact]
    public async Task Density_measures_storage_slots_and_preserves_unknown_cube_capacity()
    {
        var warehouseId = await WarehouseAsync(); Guid occupiedId, itemId;
        using (var scope = fixture.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            var area = new Area { WarehouseId = warehouseId, Code = "STOR", Name = "Storage" }; var zone = new Zone { AreaId = area.Id, Code = "AMB", Name = "Ambient" };
            master.Areas.Add(area); master.Zones.Add(zone);
            var prefix = $"DASH-{warehouseId:N}";
            Location Location(string suffix, LocationType type, bool active, decimal? volume) => new() { WarehouseId = warehouseId, ZoneId = zone.Id, Code = prefix + suffix, Type = type, IsActive = active, MaxVolumeM3 = volume };
            var occupied = Location("-FULL", LocationType.Bulk, true, 10); occupiedId = occupied.Id;
            master.Locations.AddRange(occupied, Location("-EMPTY", LocationType.PickFace, true, null), Location("-INACTIVE", LocationType.Bulk, false, 10), Location("-DOCK", LocationType.ReceivingDock, true, 10),
                new Location { WarehouseId = warehouseId, Code = prefix + "-SUPPLIER", Type = LocationType.Supplier });
            var item = new Item { Sku = prefix, Description = "Dashboard test", BaseUom = "EA" }; itemId = item.Id;
            item.PackConfigurations.Add(new PackConfiguration { ItemId = item.Id, Level = PackagingLevel.Each, BaseUnitQuantity = 1, LengthMm = 1000, WidthMm = 1000, HeightMm = 1000 });
            master.Items.Add(item); await master.SaveChangesAsync();
            var posted = await scope.ServiceProvider.GetRequiredService<IStockPosting>().ReceiveAsync(new ReceiptInstruction {
                ItemId = itemId, ToLocationId = occupiedId, Quantity = 2, IdempotencyKey = Guid.CreateVersion7().ToString(), Actor = new PostingActor(Guid.Empty, "dashboard-test") }, CancellationToken.None);
            Assert.True(posted.IsSuccess, posted.IsFailure ? posted.Error.Code : "");
        }
        using var client = await ClientAsync();
        var response = await client.GetAsync($"/api/v1/inventory/zone-density?warehouseId={warehouseId}"); response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>(); var overall = data.GetProperty("overall");
        Assert.Equal(2, overall.GetProperty("locations").GetInt32()); Assert.Equal(1, overall.GetProperty("occupied").GetInt32()); Assert.Equal(0.5m, overall.GetProperty("occupancyRate").GetDecimal());
        Assert.Equal(JsonValueKind.Null, overall.GetProperty("cubeUtilization").ValueKind);
        Assert.Equal("STOR", data.GetProperty("zones")[0].GetProperty("areaCode").GetString());
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Locations.Where(l => l.WarehouseId == warehouseId && l.Type == LocationType.PickFace)
                .ExecuteUpdateAsync(update => update.SetProperty(l => l.MaxVolumeM3, 10));
        var known = await client.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/zone-density?warehouseId={warehouseId}");
        Assert.Equal(0.1m, known.GetProperty("overall").GetProperty("cubeUtilization").GetDecimal());
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Locations.Where(l => l.WarehouseId == warehouseId && (l.Type == LocationType.PickFace || l.Type == LocationType.Bulk))
                .ExecuteUpdateAsync(update => update.SetProperty(l => l.MaxVolumeM3, 0.5m));
        var overCapacity = await client.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/zone-density?warehouseId={warehouseId}");
        Assert.Equal(2m, overCapacity.GetProperty("overall").GetProperty("cubeUtilization").GetDecimal());
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().PackConfigurations.Where(p => p.ItemId == itemId).ExecuteUpdateAsync(update => update.SetProperty(p => p.LengthMm, (decimal?)null));
        var missingVolume = await client.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/zone-density?warehouseId={warehouseId}");
        Assert.Equal(JsonValueKind.Null, missingVolume.GetProperty("overall").GetProperty("cubeUtilization").ValueKind);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MasterDataDbContext>().Areas.Where(a => a.WarehouseId == warehouseId).ExecuteUpdateAsync(update => update.SetProperty(a => a.IsActive, false));
        var closed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/zone-density?warehouseId={warehouseId}");
        Assert.Equal(0, closed.GetProperty("overall").GetProperty("locations").GetInt32());

    }

    [Fact]
    public async Task Empty_warehouse_has_no_measured_density()
    {
        var warehouseId = await WarehouseAsync(); using var client = await ClientAsync();
        var response = await client.GetAsync($"/api/v1/inventory/zone-density?warehouseId={warehouseId}"); response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(JsonValueKind.Null, data.GetProperty("overall").GetProperty("occupancyRate").ValueKind);
    }
    [Fact]
    public async Task Zones_with_the_same_code_in_different_areas_remain_separate()
    {
        var warehouseId = await WarehouseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
            foreach (var code in new[] { "A", "B" })
            {
                var area = new Area { WarehouseId = warehouseId, Code = code, Name = code }; var zone = new Zone { AreaId = area.Id, Code = "AMB", Name = "Ambient" };
                db.Areas.Add(area); db.Zones.Add(zone); db.Locations.Add(new Location { WarehouseId = warehouseId, ZoneId = zone.Id, Type = LocationType.Bulk, Code = $"{warehouseId:N}-{code}-AMB-01" });
            }
            await db.SaveChangesAsync();
        }
        using var client = await ClientAsync(); var data = await client.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/zone-density?warehouseId={warehouseId}");
        var zones = data.GetProperty("zones").EnumerateArray().ToList(); Assert.Equal(2, zones.Count);
        Assert.Equal(new[] { "A", "B" }, zones.Select(z => z.GetProperty("areaCode").GetString()));
        Assert.Equal(2, zones.Select(z => z.GetProperty("zoneId").GetGuid()).Distinct().Count());
    }
    private async Task<Guid> WarehouseAsync()
    {
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();
        var warehouse = new Warehouse { Code = "D" + Guid.NewGuid().ToString("N")[..8], Name = "Dashboard test" }; db.Warehouses.Add(warehouse); await db.SaveChangesAsync(); return warehouse.Id;
    }
    private async Task<HttpClient> ClientAsync()
    {
        const string secret = "DashboardTest#2026"; const string name = "dashboard-supervisor";
        using (var scope = fixture.Services.CreateScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CountflowUser>>();
            if (await users.FindByNameAsync(name) is null) { var user = new CountflowUser { UserName = name, Email = "dashboard@example.test", EmailConfirmed = true, DisplayName = name, OperatorCode = name };
                Assert.True((await users.CreateAsync(user, secret)).Succeeded); Assert.True((await users.AddToRoleAsync(user, Roles.Supervisor)).Succeeded); }
        }
        var client = fixture.CreateClient(); var login = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(name, secret)); login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()); return client;
    }
    private static object Credentials(string identifier, string secret) => new { identifier, password = secret };
}
