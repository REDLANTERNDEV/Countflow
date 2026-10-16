using Countflow.Modules.Outbound.Domain;
using Countflow.Modules.Outbound.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Countflow.Modules.Outbound.Features.Orders;

/// <summary>Serializes order orchestration while each module retains its own transaction.</summary>
internal static class OrderMutationLease
{
    public static async Task<IAsyncDisposable> AcquireAsync(OutboundDbContext db, ModuleSession session, Guid orderId, CancellationToken cancellationToken)
    {
        var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
        { Pooling = false, ApplicationName = $"countflow.order:{orderId:N}" };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        IDisposable? pin = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(hashtextextended(@name, 2026))";
            command.Parameters.AddWithValue("name", $"countflow.order:{orderId:N}");
            await command.ExecuteNonQueryAsync(cancellationToken);
            pin = session.Pin(connection);
            OrderMutationFence? fence = null;
            if (await db.SalesOrders.AnyAsync(order => order.Id == orderId, cancellationToken))
            {
                fence = await db.OrderMutationFences.SingleOrDefaultAsync(row => row.OrderId == orderId, cancellationToken);
                if (fence is null)
                {
                    fence = new OrderMutationFence { OrderId = orderId };
                    db.OrderMutationFences.Add(fence);
                }
                await db.SaveChangesAsync(cancellationToken);
            }
            return new Lease(db, connection, fence, pin);
        }
        catch { pin?.Dispose(); await connection.DisposeAsync(); throw; }
    }

    private sealed class Lease(OutboundDbContext db, NpgsqlConnection connection, OrderMutationFence? fence, IDisposable pin) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (fence is not null) db.Entry(fence).State = EntityState.Detached;
            try { pin.Dispose(); } finally { await connection.DisposeAsync(); }
        }
    }
}

public sealed class OrderMutationFilter(OutboundDbContext db, ModuleSession session) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        await using var lease = await OrderMutationLease.AcquireAsync(db, session, context.GetArgument<Guid>(0), context.HttpContext.RequestAborted);
        return await next(context);
    }
}
