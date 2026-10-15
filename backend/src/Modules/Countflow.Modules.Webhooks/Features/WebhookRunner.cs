using System.Text.Json;
using Countflow.Modules.Webhooks.Domain;
using Countflow.Modules.Webhooks.Persistence;
using Countflow.Modules.Webhooks.Transport;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
namespace Countflow.Modules.Webhooks.Features;

public sealed class WebhookCatalog(WebhooksDbContext db) : IBackgroundJobCatalog
{
    public async Task<IReadOnlyList<BackgroundJobRegistration>> ListAsync(CancellationToken cancellationToken)
    {
        var names = (await db.Subscriptions.AsNoTracking().Where(s => s.Enabled && s.Verified).Select(s => s.Id).ToListAsync(cancellationToken))
            .Select(id => $"webhooks.outbound.{id:N}").ToList();
        names.AddRange((await db.Requests.AsNoTracking().Where(r => r.FinishedAt == null).OrderBy(r => r.CreatedAt).Take(100).Select(r => r.Id).ToListAsync(cancellationToken))
            .Select(id => $"webhooks.inbound.{id:N}"));
        return names.Select(name => new BackgroundJobRegistration(name, typeof(WebhookRunner), new RecurringJobOptions
            { Enabled = true, Interval = TimeSpan.FromMinutes(1), InitialDelay = TimeSpan.Zero })).ToList();
    }
}
internal static class WebhookLease
{
    public static async Task<IAsyncDisposable> AcquireAsync(WebhooksDbContext db, string name, Guid id, bool inbound, CancellationToken ct)
    {
        var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false, ApplicationName = name };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(ct); await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(hashtextextended(@name, 2026))"; command.Parameters.AddWithValue("name", name); await command.ExecuteNonQueryAsync(ct);
            if (inbound) await db.Requests.SingleOrDefaultAsync(r => r.Id == id, ct);
            else await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
            await db.SaveChangesAsync(ct);
            return new Lease(db, connection);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private sealed class Lease(WebhooksDbContext db, NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { db.ChangeTracker.Clear(); await connection.DisposeAsync(); }
    }
}
public sealed class WebhookRunner(WebhooksDbContext db, IStockChangeFeed feed, IWebhookTransport transport,
    WebhookSecrets secrets, TimeProvider clock, IEnumerable<BackgroundJobRegistration> registrations, IServiceProvider services) : IBackgroundJob
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task ExecuteAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        var inbound = run.Name.StartsWith("webhooks.inbound.", StringComparison.Ordinal);
        var id = Guid.ParseExact(run.Name[(run.Name.LastIndexOf('.') + 1)..], "N");
        await using var lease = await WebhookLease.AcquireAsync(db, run.Name, id, inbound, cancellationToken);
        if (inbound)
        {
            var request = await db.Requests.SingleAsync(r => r.Id == id, cancellationToken);
            if (request.FinishedAt != null) return;
            var registration = registrations.Single(r => r.Name == "inventory.reconciliation");
            var handler = (IBackgroundJob)services.GetRequiredService(registration.HandlerType);
            await handler.ExecuteAsync(new BackgroundJobRun(registration.Name, request.Id, request.CreatedAt), cancellationToken);
            request.FinishedAt = clock.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return;
        }
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is not { Enabled: true, Verified: true }) return;
        Delivery? delivery = subscription.PendingDeliveryId is { } pending ? await db.Deliveries.SingleAsync(d => d.Id == pending, cancellationToken) : null;
        if (delivery is null)
        {
            var batch = await feed.ReadAsync(subscription.Cursor, 100, cancellationToken);
            if (batch.Changes.Count == 0) return;
            var deliveryId = Guid.CreateVersion7(); var now = clock.GetUtcNow();
            delivery = new Delivery { Id = deliveryId, SubscriptionId = subscription.Id, NextCursor = batch.NextCursor, CreatedAt = now, NextAttemptAt = now,
                Body = JsonSerializer.Serialize(new { id = deliveryId, type = "inventory.changes", createdAt = now, fromCursor = subscription.Cursor,
                    nextCursor = batch.NextCursor, watermark = batch.Watermark, changes = batch.Changes }, Json) };
            db.Deliveries.Add(delivery); subscription.PendingDeliveryId = delivery.Id;
            // Persist the immutable body and cursor in one local transaction before sending.
            subscription.Cursor = batch.NextCursor; await db.SaveChangesAsync(cancellationToken);
        }
        if (delivery.State != DeliveryState.Pending || delivery.NextAttemptAt > clock.GetUtcNow()) return;
        if (delivery.Attempts >= 8)
        {
            delivery.State = DeliveryState.DeadLetter;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        delivery.Attempts++;
        delivery.NextAttemptAt = clock.GetUtcNow().AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(delivery.Attempts - 1, 6))));
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var reply = await transport.SendAsync(subscription.Url, delivery.Body, secrets.Reveal(subscription.ProtectedSecret), delivery.Id, "inventory.changes", cancellationToken);
            delivery.LastStatus = reply.Status;
            if (reply.Status is >= 200 and < 300)
            {
                delivery.State = DeliveryState.Delivered; delivery.DeliveredAt = clock.GetUtcNow(); delivery.LastErrorCode = null; subscription.PendingDeliveryId = null;
            }
            else delivery.LastErrorCode = "webhooks.receiver_refused";
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not DbUpdateConcurrencyException)
        { delivery.LastErrorCode = "webhooks.delivery_failed"; delivery.LastStatus = null; }
        if (delivery.State == DeliveryState.Pending && delivery.Attempts >= 8) delivery.State = DeliveryState.DeadLetter;
        await db.SaveChangesAsync(cancellationToken);
    }
}
