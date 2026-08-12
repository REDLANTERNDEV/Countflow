using Countflow.Modules.Identity.Authentication;
using Countflow.Modules.Identity.Domain;
using Countflow.SharedKernel.Modules;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Identity.Persistence;

public sealed class IdentitySeeder(
    UserManager<CountflowUser> userManager,
    RoleManager<CountflowRole> roleManager,
    IdentityModuleDbContext dbContext,
    IConfiguration configuration,
    ILogger<IdentitySeeder> logger) : IModuleSeeder
{
    /// <summary>The account name the demo stack's simulator authenticates as, for a given role.</summary>
    public static string IntegrationAccountName(string role) => $"demo-{role.ToLowerInvariant()}-integration";

    public int Order => 0;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new CountflowRole(role));
                logger.LogInformation("Created role {Role}", role);
            }
        }

        // Demo accounts exist so `docker compose up` gives a usable system. They
        // are gated behind a flag that defaults off, so a production deployment
        // never silently gains a known-password admin.
        if (!configuration.GetValue("Seed:DemoUsers", false))
        {
            return;
        }

        await EnsureUserAsync("admin", "admin@countflow.local", "Warehouse Admin", "ADM001", Roles.Admin);
        await EnsureUserAsync("supervisor", "supervisor@countflow.local", "Floor Supervisor", "SUP001", Roles.Supervisor);
        await EnsureUserAsync("operator", "operator@countflow.local", "Picker One", "OP001", Roles.Operator);

        foreach (var role in Roles.All)
        {
            await EnsureIntegrationAsync(role);
        }
    }

    /// <summary>
    /// One integration account per role, for the demo stack's simulator.
    /// <para>
    /// <b>Three accounts rather than one, and that is load-bearing.</b> Promoting a
    /// rule set is admin-only, releasing a wave is supervisor-and-above, and
    /// confirming a task is an operator's job. A single all-powerful integration
    /// would sail through all three and never notice if one of those checks were
    /// wrong — which is exactly what happened in Phase 7, when the simulator ran
    /// with an administrator's client in the supervisor's slot and four simulated
    /// days quietly produced no counts at all. A client whose credentials are
    /// stronger than the ones it claims to use is not testing the API an integrator
    /// has.
    /// </para>
    /// <para>
    /// Each key is <i>supplied</i> rather than minted, which is the one thing the
    /// API itself will not let you do — for a good reason that does not apply here.
    /// A minted key is shown once and then exists only as a hash, so nothing can
    /// hand it to a container started by the same <c>docker compose up</c>. The
    /// demo needs a value known to both sides before either starts, so it comes
    /// from the environment, and it is the operator's job to make it random.
    /// </para>
    /// </summary>
    private async Task EnsureIntegrationAsync(string role)
    {
        var secret = configuration[$"Seed:DemoApiKey:{role}"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        // 32 characters is not 256 bits, but it is enough to catch "changeme" and
        // an accidentally half-set variable, which are the failures that actually
        // happen. Refusing loudly beats seeding a guessable machine credential.
        if (secret.Trim().Length < 32)
        {
            throw new InvalidOperationException(
                $"Seed:DemoApiKey:{role} is too short. Use at least 32 random characters — "
                + "it is a machine credential with a real role behind it.");
        }

        var name = IntegrationAccountName(role);
        var account = await userManager.FindByNameAsync(name);

        if (account is null)
        {
            account = new CountflowUser
            {
                UserName = name,
                Email = $"{name}@service.countflow.local",
                EmailConfirmed = true,
                DisplayName = $"Demo integration ({role})",
                OperatorCode = $"SVC-{role.ToUpperInvariant()[..3]}",
                IsServiceAccount = true
            };

            // No password, exactly as ServiceAccountService creates one.
            var created = await userManager.CreateAsync(account);
            if (!created.Succeeded)
            {
                var errors = string.Join("; ", created.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Could not seed the demo integration '{name}': {errors}");
            }

            await userManager.AddToRoleAsync(account, role);
            logger.LogInformation("Seeded demo service account {UserName} with role {Role}", name, role);
        }

        var described = ApiKeySecret.Describe(secret);

        if (await dbContext.ApiKeys.AnyAsync(key => key.SecretHash == described.Hash))
        {
            return;
        }

        dbContext.ApiKeys.Add(new ApiKey
        {
            UserId = account.Id,
            Name = "Demo stack",
            SecretHash = described.Hash,
            Prefix = described.Prefix,
            CreatedByUserId = account.Id
        });

        await dbContext.SaveChangesAsync();
        logger.LogInformation("Registered demo API key {Prefix} for {UserName}", described.Prefix, name);
    }

    private async Task EnsureUserAsync(
        string userName,
        string email,
        string displayName,
        string operatorCode,
        string role)
    {
        if (await userManager.FindByNameAsync(userName) is not null)
        {
            return;
        }

        var password = configuration["Seed:DemoPassword"] ?? "Countflow#2026";

        var user = new CountflowUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            OperatorCode = operatorCode
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            var errors = string.Join("; ", created.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Could not seed demo user '{userName}': {errors}");
        }

        await userManager.AddToRoleAsync(user, role);
        logger.LogInformation("Seeded demo user {UserName} with role {Role}", userName, role);
    }
}
