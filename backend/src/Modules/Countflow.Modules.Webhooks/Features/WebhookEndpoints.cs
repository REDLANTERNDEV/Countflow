using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Countflow.Modules.Webhooks.Domain;
using Countflow.Modules.Webhooks.Persistence;
using Countflow.Modules.Webhooks.Transport;
using Countflow.SharedKernel.Results;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
namespace Countflow.Modules.Webhooks.Features;

public sealed record RegisterWebhook(string Name, string Url, long AfterSequence = 0);
public sealed record WebhookResponse(Guid Id, string Name, string Url, bool Verified, bool Enabled, long Cursor, Guid? PendingDeliveryId, DeliveryState? PendingState = null);
public sealed record IssuedWebhook(WebhookResponse Subscription, string Secret);
public sealed record SetWebhookEnabled(bool Enabled);
public sealed record DeliveryResponse(Guid Id, DeliveryState State, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset NextAttemptAt, DateTimeOffset? DeliveredAt, int? LastStatus, string? LastErrorCode, long NextCursor);
public sealed record RegisterTrigger(string Name);
public sealed record TriggerResponse(Guid Id, string Name, bool Enabled, int PendingRequests, DateTimeOffset? LastFinishedAt);
public sealed record IssuedTrigger(Guid Id, string Name, string Url);
public sealed record AcceptedTrigger(Guid RequestId);
public static class WebhookEndpoints
{
    private static WebhookResponse Describe(Subscription s) => new(s.Id, s.Name, s.Url, s.Verified, s.Enabled, s.Cursor, s.PendingDeliveryId);
    private static IResult Refuse(string code, string message) => Result.Failure(Error.Validation(code, message)).ToHttpResult();
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/webhooks").WithTags("Webhooks").RequireAuthorization(p => p.RequireRole(Roles.Admin));
        group.MapGet("/subscriptions", async (WebhooksDbContext db, CancellationToken ct) =>
        {
            var subscriptions = await db.Subscriptions.AsNoTracking().OrderBy(s => s.Name).Take(100).ToListAsync(ct);
            var pendingIds = subscriptions.Where(s => s.PendingDeliveryId != null).Select(s => s.PendingDeliveryId!.Value).ToList();
            var states = await db.Deliveries.AsNoTracking().Where(d => pendingIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.State, ct);
            return Results.Ok(subscriptions.Select(s => Describe(s) with { PendingState = s.PendingDeliveryId is { } pending ? states.GetValueOrDefault(pending) : null }).ToList());
        }).WithName("GetWebhookSubscriptions").Produces<IReadOnlyList<WebhookResponse>>();
        group.MapPost("/subscriptions", async (RegisterWebhook request, WebhooksDbContext db, ReceiverPolicy policy, WebhookSecrets secrets, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || request.AfterSequence < 0 || !policy.Accepts(request.Url)) return Refuse("webhooks.receiver_invalid", "Use a public HTTPS receiver and a name up to 100 characters.");
            if (await db.Subscriptions.CountAsync(ct) >= 100) return Refuse("webhooks.limit_reached", "At most 100 subscriptions.");
            var secret = WebhookSecrets.Generate(); string encrypted;
            try { encrypted = secrets.Protect(secret); } catch (Exception e) when (e is InvalidOperationException or FormatException) { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "webhooks.key_not_configured" }); }
            var subscription = new Subscription { Name = request.Name.Trim(), Url = request.Url, ProtectedSecret = encrypted, Cursor = request.AfterSequence };
            db.Subscriptions.Add(subscription); await db.SaveChangesAsync(ct); return Results.Ok(new IssuedWebhook(Describe(subscription), secret));
        }).WithName("RegisterWebhookSubscription").Produces<IssuedWebhook>().ProducesProblem(400).ProducesProblem(503);
        group.MapPost("/subscriptions/{id:guid}/verify", async (Guid id, WebhooksDbContext db, IWebhookTransport transport, WebhookSecrets secrets, CancellationToken ct) =>
        {
            await using var lease = await WebhookLease.AcquireAsync(db, $"webhooks.outbound.{id:N}", id, false, ct);
            var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
            if (subscription is null) return Results.NotFound();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                var reply = await transport.SendAsync(subscription.Url, JsonSerializer.Serialize(new { type = "webhook.verify", challenge = nonce }), secrets.Reveal(subscription.ProtectedSecret), Guid.CreateVersion7(), "webhook.verify", ct);
                if (reply.Status != 200 || reply.Body != nonce) return Refuse("webhooks.verification_failed", "Receiver must echo the signed challenge as plain text.");
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not DbUpdateConcurrencyException) { return Refuse("webhooks.verification_failed", "Receiver could not be verified."); }
            subscription.Verified = true; await db.SaveChangesAsync(ct); return Results.Ok(Describe(subscription));
        }).WithName("VerifyWebhookReceiver").Produces<WebhookResponse>().ProducesProblem(400).ProducesProblem(404);
        group.MapPut("/subscriptions/{id:guid}", async (Guid id, SetWebhookEnabled request, WebhooksDbContext db, CancellationToken ct) =>
        {
            await using var lease = await WebhookLease.AcquireAsync(db, $"webhooks.outbound.{id:N}", id, false, ct);
            var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
            if (subscription is null) return Results.NotFound();
            if (request.Enabled && !subscription.Verified) return Refuse("webhooks.verification_required", "Verify the receiver before enabling it.");
            subscription.Enabled = request.Enabled; await db.SaveChangesAsync(ct); return Results.Ok(Describe(subscription));
        }).WithName("SetWebhookEnabled").Produces<WebhookResponse>().ProducesProblem(400).ProducesProblem(404);
        group.MapGet("/subscriptions/{id:guid}/deliveries", async (Guid id, WebhooksDbContext db, int? page, CancellationToken ct) => Results.Ok(await db.Deliveries.AsNoTracking().Where(d => d.SubscriptionId == id).OrderByDescending(d => d.CreatedAt).Skip((Math.Clamp(page ?? 1, 1, 1_000_000) - 1) * 25).Take(25)
            .Select(d => new DeliveryResponse(d.Id, d.State, d.Attempts, d.CreatedAt, d.NextAttemptAt, d.DeliveredAt, d.LastStatus, d.LastErrorCode, d.NextCursor)).ToListAsync(ct)))
            .WithName("GetWebhookDeliveries").Produces<IReadOnlyList<DeliveryResponse>>();
        group.MapPost("/subscriptions/{id:guid}/retry", async (Guid id, WebhooksDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            await using var lease = await WebhookLease.AcquireAsync(db, $"webhooks.outbound.{id:N}", id, false, ct);
            var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
            if (subscription?.PendingDeliveryId is not { } pending) return Results.NotFound();
            var delivery = await db.Deliveries.SingleAsync(d => d.Id == pending, ct);
            delivery.State = DeliveryState.Pending; delivery.Attempts = 0; delivery.NextAttemptAt = clock.GetUtcNow(); delivery.LastErrorCode = null;
            await db.SaveChangesAsync(ct); return Results.Ok(Describe(subscription));
        }).WithName("RetryWebhookDelivery").Produces<WebhookResponse>().ProducesProblem(404);
        group.MapGet("/triggers", async (WebhooksDbContext db, CancellationToken ct) => Results.Ok(await db.Triggers.AsNoTracking().OrderBy(t => t.Name).Take(100).Select(t => new TriggerResponse(t.Id, t.Name, t.Enabled, db.Requests.Count(r => r.TriggerId == t.Id && r.FinishedAt == null), db.Requests.Where(r => r.TriggerId == t.Id).Max(r => r.FinishedAt))).ToListAsync(ct)))
            .WithName("GetWebhookTriggers").Produces<IReadOnlyList<TriggerResponse>>();
        group.MapPost("/triggers", async (RegisterTrigger request, WebhooksDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || await db.Triggers.CountAsync(ct) >= 100) return Refuse("webhooks.limit_reached", "Use a name up to 100 characters and at most 100 triggers.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var trigger = new InboundTrigger { Name = request.Name.Trim(), TokenHash = WebhookSecrets.Hash(token) };
            db.Triggers.Add(trigger); await db.SaveChangesAsync(ct);
            return Results.Ok(new IssuedTrigger(trigger.Id, trigger.Name, $"/api/v1/webhooks/invoke/{trigger.Id}/{token}"));
        }).WithName("RegisterWebhookTrigger").Produces<IssuedTrigger>().ProducesProblem(400);
        group.MapDelete("/triggers/{id:guid}", async (Guid id, WebhooksDbContext db, CancellationToken ct) => { var changed = await db.Triggers.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Enabled, false), ct); return changed == 0 ? Results.NotFound() : Results.NoContent(); })
            .WithName("RevokeWebhookTrigger").Produces(204).ProducesProblem(404);
        // This capability route deliberately overrides the administrative group's auth.
        group.MapPost("/invoke/{id:guid}/{token}", async (Guid id, string token, HttpRequest http, WebhooksDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (token.Length != 64) return Results.NotFound();
            var trigger = await db.Triggers.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Enabled, ct);
            if (trigger is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(trigger.TokenHash), Encoding.ASCII.GetBytes(WebhookSecrets.Hash(token)))) return Results.NotFound();
            var key = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 128) return Refuse("webhooks.retry_key_required", "Supply a retry key up to 128 characters.");
            var existing = await db.Requests.AsNoTracking().SingleOrDefaultAsync(r => r.TriggerId == id && r.RetryKey == key, ct);
            if (existing is not null) return Results.Accepted(value: new AcceptedTrigger(existing.Id));
            if (await db.Requests.CountAsync(r => r.FinishedAt == null, ct) >= 100) return Results.StatusCode(429);
            var request = new TriggerRequest { TriggerId = id, RetryKey = key, CreatedAt = clock.GetUtcNow() };
            db.Requests.Add(request);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            { db.ChangeTracker.Clear(); request = await db.Requests.AsNoTracking().SingleAsync(r => r.TriggerId == id && r.RetryKey == key, ct); }
            return Results.Accepted(value: new AcceptedTrigger(request.Id));
        }).AllowAnonymous().WithName("InvokeWebhookTrigger").Produces<AcceptedTrigger>(202).ProducesProblem(400).ProducesProblem(404).Produces(429);
    }
}
