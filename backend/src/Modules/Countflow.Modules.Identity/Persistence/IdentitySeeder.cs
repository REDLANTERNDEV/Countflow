using Countflow.Modules.Identity.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Countflow.Modules.Identity.Persistence;

public sealed class IdentitySeeder(
    UserManager<CountflowUser> userManager,
    RoleManager<CountflowRole> roleManager,
    IConfiguration configuration,
    ILogger<IdentitySeeder> logger) : IModuleSeeder
{
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
