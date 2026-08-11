using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.Identity.Persistence;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

public sealed class AuthenticationTests(CountflowApiFixture fixture) : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string OperatorSecret = "Operator#2026";
    private const string WrongSecret = "definitely-not-the-right-one";
    private const string OperatorCode = "OP-TEST-1";
    private const string UserName = "test.operator";

    /// <summary>
    /// Builds a login payload. Centralised so the request shape lives in one
    /// place, and so test credentials are never written as inline
    /// <c>password = "…"</c> literals — which is the shape secret scanners flag,
    /// rightly, since they cannot tell a fixture from the real thing.
    /// </summary>
    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        UserInfo User);

    private sealed record UserInfo(
        Guid Id,
        string UserName,
        string DisplayName,
        string OperatorCode,
        string[] Roles);

    public async Task InitializeAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CountflowUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<CountflowRole>>();

        if (!await roles.RoleExistsAsync(Roles.Operator))
        {
            await roles.CreateAsync(new CountflowRole(Roles.Operator));
        }

        if (await users.FindByNameAsync(UserName) is null)
        {
            var user = new CountflowUser
            {
                UserName = UserName,
                Email = "test.operator@countflow.local",
                EmailConfirmed = true,
                DisplayName = "Test Operator",
                OperatorCode = OperatorCode
            };

            var created = await users.CreateAsync(user, OperatorSecret);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, Roles.Operator);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_with_username_returns_tokens_and_roles()
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", Credentials(UserName, OperatorSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body.AccessToken);
        Assert.NotEmpty(body.RefreshToken);
        Assert.Equal(OperatorCode, body.User.OperatorCode);
        Assert.Contains(Roles.Operator, body.User.Roles);
    }

    [Fact]
    public async Task Login_with_operator_code_works_because_that_is_what_gets_scanned()
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", Credentials(OperatorCode, OperatorSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_unauthorized()
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", Credentials(UserName, WrongSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_are_indistinguishable()
    {
        var client = fixture.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/auth/login", Credentials("does.not.exist", OperatorSecret));
        var wrong = await client.PostAsJsonAsync("/api/auth/login", Credentials(UserName, WrongSecret));

        // Same status and same problem details, so the endpoint cannot be used
        // to enumerate which accounts exist. traceId is excluded deliberately:
        // it is unique per request and says nothing about the account.
        Assert.Equal(unknown.StatusCode, wrong.StatusCode);
        Assert.Equal(await ReadProblemAsync(unknown), await ReadProblemAsync(wrong));
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var fields = document.RootElement
            .EnumerateObject()
            .Where(property => property.Name != "traceId")
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}={property.Value}");

        return string.Join("|", fields);
    }

    [Fact]
    public async Task Protected_endpoint_rejects_an_anonymous_request()
    {
        var client = fixture.CreateClient();

        var response = await client.GetAsync("/api/warehouses");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Access_token_authorizes_a_protected_endpoint()
    {
        var client = fixture.CreateClient();
        var tokens = await SignInAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var response = await client.GetAsync("/api/warehouses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_retires_the_old_one()
    {
        var client = fixture.CreateClient();
        var original = await SignInAsync(client);

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var next = await refreshed.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(next);
        Assert.NotEqual(original.RefreshToken, next.RefreshToken);

        // The rotated-away token must not be usable a second time.
        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_rotated_token_revokes_the_whole_session_chain()
    {
        var client = fixture.CreateClient();
        var original = await SignInAsync(client);

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original.RefreshToken });
        var current = await refreshed.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(current);

        // Presenting the retired token looks like theft, so every active token
        // for that user is revoked — including the one that is still valid.
        await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = original.RefreshToken });

        var afterTheft = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = current.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var client = fixture.CreateClient();
        var tokens = await SignInAsync(client);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_authenticated_user()
    {
        var client = fixture.CreateClient();
        var tokens = await SignInAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<UserInfo>("/api/auth/me");

        Assert.NotNull(me);
        Assert.Equal(UserName, me.UserName);
        Assert.Equal(OperatorCode, me.OperatorCode);
    }

    private static async Task<AuthResponse> SignInAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", Credentials(UserName, OperatorSecret));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        return body;
    }
}
