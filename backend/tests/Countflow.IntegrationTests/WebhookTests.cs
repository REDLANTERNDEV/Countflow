using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Countflow.Modules.Webhooks.Features;
using Countflow.Modules.Webhooks.Persistence;
using Countflow.Modules.Webhooks.Transport;
using Countflow.Modules.Webhooks.Domain;
using Countflow.SharedKernel.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using System.Text;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;
using Countflow.Modules.Identity.Domain;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

public sealed class WebhookTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>
{
    [Fact]
    public async Task Only_admins_can_manage_webhooks()
    {
        using var supervisor = await ClientAsync(Roles.Supervisor);
        var response = await supervisor.GetAsync("/api/v1/webhooks/subscriptions");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Registration_refuses_an_internal_receiver_and_never_echoes_secrets_in_lists()
    {
        using var admin = await ClientAsync(Roles.Admin);
        var refused = await admin.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new { name = "internal", url = "http://127.0.0.1:8080/health/ready", afterSequence = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var list = await admin.GetAsync("/api/v1/webhooks/subscriptions");
        list.EnsureSuccessStatusCode();
        Assert.DoesNotContain("secret", await list.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confidential_trigger_requires_a_retry_key_and_reuses_its_durable_request()
    {
        using var admin = await ClientAsync(Roles.Admin);
        var created = await admin.PostAsJsonAsync("/api/v1/webhooks/triggers", new { name = "reconcile" });
        created.EnsureSuccessStatusCode();
        var issued = await created.Content.ReadFromJsonAsync<JsonElement>();
        var url = issued.GetProperty("url").GetString()!;
        var rejected = await fixture.CreateClient().PostAsJsonAsync(url, new { });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var key = Guid.CreateVersion7().ToString();
        async Task<Guid> Invoke()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
            request.Headers.Add("Idempotency-Key", key);
            var response = await fixture.CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetGuid();
        }
        Assert.Equal(await Invoke(), await Invoke());
    }

    [Fact]
    public async Task Verified_deliveries_retry_the_exact_signed_body_and_block_at_dead_letter()
    {
        var sender = new TestSender();
        using var host = fixture.WithWebHostBuilder(builder => {
            builder.UseSetting("Webhooks:EncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.ConfigureServices(services => { services.RemoveAll<IWebhookTransport>(); services.AddSingleton<IWebhookTransport>(sender);
                services.RemoveAll<IStockChangeFeed>(); services.AddSingleton<IStockChangeFeed>(new TestFeed()); });
        });
        using var credential = await ClientAsync(Roles.Admin);
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Authorization = credential.DefaultRequestHeaders.Authorization;
        var issuedResponse = await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new { name = "receiver", url = "https://receiver.example.test/hook" });
        issuedResponse.EnsureSuccessStatusCode();
        var issued = await issuedResponse.Content.ReadFromJsonAsync<IssuedWebhook>(); var id = issued!.Subscription.Id;
        var unverified = await client.PutAsJsonAsync($"/api/v1/webhooks/subscriptions/{id}", new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, unverified.StatusCode);
        (await client.PostAsync($"/api/v1/webhooks/subscriptions/{id}/verify", null)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/v1/webhooks/subscriptions/{id}", new { enabled = true })).EnsureSuccessStatusCode();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>();
            await db.Deliveries.Where(d => d.SubscriptionId == id).ExecuteUpdateAsync(update => update.SetProperty(d => d.NextAttemptAt, DateTimeOffset.MinValue));
            await scope.ServiceProvider.GetRequiredService<WebhookRunner>().ExecuteAsync(new BackgroundJobRun($"webhooks.outbound.{id:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        }
        Assert.Equal(8, sender.Bodies.Count); Assert.Single(sender.Bodies.Distinct()); Assert.Single(sender.Ids.Distinct());
        var signature = WebhookSecrets.Sign(sender.Bodies[0], issued.Secret);
        Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(issued.Secret), Encoding.UTF8.GetBytes(sender.Bodies[0]))), signature);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>(); var delivery = await db.Deliveries.SingleAsync(d => d.SubscriptionId == id);
            Assert.Equal(DeliveryState.DeadLetter, delivery.State);
            // Simulate process loss after the eighth pre-send attempt save.
            delivery.State = DeliveryState.Pending; delivery.NextAttemptAt = DateTimeOffset.MinValue; await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<WebhookRunner>().ExecuteAsync(new BackgroundJobRun($"webhooks.outbound.{id:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.Equal(8, sender.Bodies.Count);
        }
        sender.Status = 200;
        (await client.PostAsync($"/api/v1/webhooks/subscriptions/{id}/retry", null)).EnsureSuccessStatusCode();
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WebhookRunner>().ExecuteAsync(new BackgroundJobRun($"webhooks.outbound.{id:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>(); Assert.Equal(DeliveryState.Delivered, (await db.Deliveries.SingleAsync(d => d.SubscriptionId == id)).State);
            Assert.Null((await db.Subscriptions.FindAsync(id))!.PendingDeliveryId);
        }
        Assert.Single(sender.Bodies.Distinct()); Assert.Single(sender.Ids.Distinct());
        var listed = await client.GetStringAsync("/api/v1/webhooks/subscriptions"); Assert.DoesNotContain(issued.Secret, listed);
    }

    [Fact]
    public async Task A_bad_challenge_echo_never_activates_a_receiver()
    {
        var sender = new TestSender { EchoChallenge = false };
        using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IWebhookTransport>(); services.AddSingleton<IWebhookTransport>(sender); }));
        using var credential = await ClientAsync(Roles.Admin); using var client = host.CreateClient(); client.DefaultRequestHeaders.Authorization = credential.DefaultRequestHeaders.Authorization;
        var issued = await (await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new { name = "bad echo", url = "https://receiver.example.test/hook" })).Content.ReadFromJsonAsync<IssuedWebhook>();
        Assert.NotNull(issued);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/v1/webhooks/subscriptions/{issued.Subscription.Id}/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/webhooks/subscriptions/{issued.Subscription.Id}", new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task Lost_subscription_lease_fences_a_stale_batch_before_network_effects()
    {
        var feed = new BlockingFeed(); var sender = new TestSender();
        using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.RemoveAll<IWebhookTransport>(); services.AddSingleton<IWebhookTransport>(sender);
            services.RemoveAll<IStockChangeFeed>(); services.AddSingleton<IStockChangeFeed>(feed); }));
        Guid id;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>(); var subscription = new Subscription {
                Name = "lease fence", Url = "https://receiver.example.test/hook", ProtectedSecret = scope.ServiceProvider.GetRequiredService<WebhookSecrets>().Protect(WebhookSecrets.Generate()), Enabled = true, Verified = true };
            db.Subscriptions.Add(subscription); await db.SaveChangesAsync(); id = subscription.Id;
        }
        using var old = host.Services.CreateScope();
        var name = $"webhooks.outbound.{id:N}";
        var stale = old.ServiceProvider.GetRequiredService<WebhookRunner>().ExecuteAsync(new BackgroundJobRun(name, Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        await feed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var control = host.Services.CreateScope())
        {
            var db = control.ServiceProvider.GetRequiredService<WebhooksDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = {name}");
        }
        using (var replacement = host.Services.CreateScope())
            await replacement.ServiceProvider.GetRequiredService<WebhookRunner>().ExecuteAsync(new BackgroundJobRun(name, Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        feed.Release.TrySetResult(); await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale);
        using var check = host.Services.CreateScope();
        Assert.Single(await check.ServiceProvider.GetRequiredService<WebhooksDbContext>().Deliveries.Where(d => d.SubscriptionId == id).ToListAsync());
        Assert.Single(sender.Bodies);
    }

    [Fact]
    public async Task Trigger_retries_converge_concurrently_and_a_durable_request_runs_once()
    {
        using var admin = await ClientAsync(Roles.Admin);
        var issued = await (await admin.PostAsJsonAsync("/api/v1/webhooks/triggers", new { name = "durable reconcile" })).Content.ReadFromJsonAsync<IssuedTrigger>();
        Assert.NotNull(issued); var key = Guid.CreateVersion7().ToString();
        async Task<Guid> Call()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, issued.Url); request.Headers.Add("Idempotency-Key", key);
            var response = await fixture.CreateClient().SendAsync(request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AcceptedTrigger>())!.RequestId;
        }
        var ids = await Task.WhenAll(Call(), Call()); Assert.Equal(ids[0], ids[1]);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = fixture.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WebhookRunner>().ExecuteAsync(new BackgroundJobRun($"webhooks.inbound.{ids[0]:N}", Guid.CreateVersion7(), DateTimeOffset.UtcNow), CancellationToken.None);
        }
        using var check = fixture.Services.CreateScope(); Assert.NotNull((await check.ServiceProvider.GetRequiredService<WebhooksDbContext>().Requests.FindAsync(ids[0]))!.FinishedAt);
        (await admin.DeleteAsync($"/api/v1/webhooks/triggers/{issued.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.CreateClient().PostAsync(issued.Url, null)).StatusCode);
        Assert.DoesNotContain(issued.Url.Split('/').Last(), await admin.GetStringAsync("/api/v1/webhooks/triggers"));
    }

    [Fact]
    public async Task A_2xx_acknowledgement_does_not_wait_for_the_receivers_body()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () => { using var socket = await listener.AcceptTcpClientAsync(); await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true); while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n")); await stream.FlushAsync(); await release.Task; });
        try
        {
            var url = $"http://127.0.0.1:{port}";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowedOrigins:0"] = url }).Build();
            var reply = await new WebhookTransport(new ReceiverPolicy(configuration)).SendAsync(url, "{}", WebhookSecrets.Generate(), Guid.CreateVersion7(), "inventory.changes", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(200, reply.Status);
        }
        finally { release.TrySetResult(); listener.Stop(); await server.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Mixed_case_capability_routes_never_write_the_token_to_application_logs()
    {
        var sink = new CaptureSink();
        using var host = fixture.WithWebHostBuilder(builder => {
            builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Information");
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(sink)); });
        using var credential = await ClientAsync(Roles.Admin); using var admin = host.CreateClient(); admin.DefaultRequestHeaders.Authorization = credential.DefaultRequestHeaders.Authorization;
        var issued = await (await admin.PostAsJsonAsync("/api/v1/webhooks/triggers", new { name = "log guard" })).Content.ReadFromJsonAsync<IssuedTrigger>(); Assert.NotNull(issued);
        using var invoke = new HttpRequestMessage(HttpMethod.Post, issued.Url.Replace("/webhooks/invoke/", "/WEBHOOKS/INVOKE/")); invoke.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        Assert.Equal(HttpStatusCode.Accepted, (await host.CreateClient().SendAsync(invoke)).StatusCode);
        Assert.NotEmpty(sink.Events);
        Assert.DoesNotContain(sink.Events, entry => entry.RenderMessage().Contains(issued.Url.Split('/').Last(), StringComparison.Ordinal));
    }
    private sealed class CaptureSink : ILogEventSink
    { public ConcurrentQueue<LogEvent> Events = new(); public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent); }

    [Theory]
    [InlineData("127.0.0.1")][InlineData("10.1.2.3")][InlineData("172.16.0.1")][InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")][InlineData("100.64.0.1")][InlineData("::1")][InlineData("::ffff:127.0.0.1")][InlineData("fc00::1")]
    public void Public_receiver_policy_refuses_internal_addresses(string address) => Assert.False(ReceiverPolicy.IsPublic(IPAddress.Parse(address)));

    private sealed class TestFeed : IStockChangeFeed
    {
        public Task<StockChangeBatch> ReadAsync(long afterSequence, int limit, CancellationToken ct) => Task.FromResult(new StockChangeBatch(
            afterSequence < 1 ? [JsonSerializer.SerializeToElement(new { sequence = 1, sku = "Çay", quantity = 3 })] : [], 1, false, 1));
    }
    private sealed class BlockingFeed : IStockChangeFeed
    {
        private int calls;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<StockChangeBatch> ReadAsync(long afterSequence, int limit, CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return await new TestFeed().ReadAsync(afterSequence, limit, ct);
        }
    }
    private sealed class TestSender : IWebhookTransport
    {
        public bool EchoChallenge = true; public int Status = 500; public List<string> Bodies = []; public List<Guid> Ids = [];
        public Task<WebhookReply> SendAsync(string url, string body, string secret, Guid id, string eventType, CancellationToken ct)
        {
            if (eventType == "webhook.verify") return Task.FromResult(new WebhookReply(200, EchoChallenge ? JsonDocument.Parse(body).RootElement.GetProperty("challenge").GetString()! : "wrong"));
            Bodies.Add(body); Ids.Add(id); return Task.FromResult(new WebhookReply(Status, ""));
        }
    }

    private async Task<HttpClient> ClientAsync(string role)
    {
        var name = $"wh-{Guid.NewGuid().ToString("N")[..16]}";
        const string secret = "WebhookTest#2026";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<CountflowUser>>();
            var user = new CountflowUser { UserName = name, Email = $"{name}@example.test", EmailConfirmed = true, DisplayName = name, OperatorCode = name };
            Assert.True((await users.CreateAsync(user, secret)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }
        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(name, secret));
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        return client;
    }
    private static object Credentials(string identifier, string secret) => new { identifier, password = secret };
}
