using Countflow.Modules.Replenishment.Domain;
using Countflow.Modules.Replenishment.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Countflow.Modules.Replenishment.Features;

internal static class PickFaceLease
{
    public static async Task<IAsyncDisposable> AcquireAsync(ReplenishmentDbContext db, Guid locationId, CancellationToken cancellationToken)
    {
        var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
        { Pooling = false, ApplicationName = $"countflow.replenishment:{locationId:N}" };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(hashtextextended(@name, 2026))";
            command.Parameters.AddWithValue("name", $"countflow.replenishment:{locationId:N}");
            await command.ExecuteNonQueryAsync(cancellationToken);
            var policy = await db.PickFacePolicies.SingleOrDefaultAsync(row => row.LocationId == locationId, cancellationToken);
            if (policy is not null) await db.SaveChangesAsync(cancellationToken);
            return new Lease(db, connection, policy);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class Lease(ReplenishmentDbContext db, NpgsqlConnection connection, PickFacePolicy? policy) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (policy is not null) db.Entry(policy).State = EntityState.Detached;
            await connection.DisposeAsync();
        }
    }
}
