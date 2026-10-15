using Countflow.Modules.Counting.Persistence;
using Countflow.Modules.Counting.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Countflow.Modules.Counting.Features.Plans;

/// <summary>Serializes planning and cancellation without spanning module writes with a transaction.</summary>
internal static class CountPlanningLock
{
    public static async Task<IAsyncDisposable> AcquireAsync(CountingDbContext db, Guid warehouseId, CancellationToken cancellationToken)
    {
        var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false, ApplicationName = $"countflow.count-planning:{warehouseId:N}" };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(hashtextextended(@name, 2026))";
            command.Parameters.AddWithValue("name", $"countflow.count-planning:{warehouseId:N}");
            await command.ExecuteNonQueryAsync(cancellationToken);
            var fence = await db.CountPlanningFences.SingleOrDefaultAsync(row => row.WarehouseId == warehouseId, cancellationToken);
            if (fence is null)
            {
                fence = new CountPlanningFence { WarehouseId = warehouseId };
                db.CountPlanningFences.Add(fence);
            }
            await db.SaveChangesAsync(cancellationToken);
            return new Lease(db, connection, fence);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(CountingDbContext db, NpgsqlConnection connection, CountPlanningFence fence) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            db.Entry(fence).State = EntityState.Detached;
            await connection.DisposeAsync();
        }
    }
}
