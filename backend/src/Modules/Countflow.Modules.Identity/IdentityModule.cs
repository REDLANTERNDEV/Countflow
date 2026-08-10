using System.Text;
using Countflow.Modules.Identity.Authentication;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.Identity.Features.Authentication;
using Countflow.Modules.Identity.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Countflow.Modules.Identity;

public sealed class IdentityModule : ICountflowModule
{
    public string Name => "Identity";

    public string Schema => IdentityModuleDbContext.SchemaName;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Countflow")
            ?? throw new InvalidOperationException("Connection string 'Countflow' is not configured.");

        services.AddModuleDbContext<IdentityModuleDbContext>(connectionString, Schema);

        // JwtOptions is deliberately *not* registered here. It is bound and
        // validated in AddCountflowJwtBearer, which only a host that actually
        // issues tokens calls. Registering it module-side meant the migrator —
        // which never mints a JWT — still had to carry a signing key just to get
        // past ValidateOnStart, so a placeholder secret sat in its appsettings
        // for no reason.
        services
            .AddIdentityCore<CountflowUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;

                // Operators type passwords on a scanner keypad wearing gloves.
                // Length and case still apply; symbols are a step too far.
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.MaxFailedAccessAttempts = 10;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddRoles<CountflowRole>()
            .AddEntityFrameworkStores<IdentityModuleDbContext>();

        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<IModuleSeeder, IdentitySeeder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => AuthEndpoints.Map(endpoints);
}

public static class IdentityModuleExtensions
{
    /// <summary>
    /// Wires JWT bearer validation for the whole host. Authentication is
    /// cross-cutting, so the host opts in explicitly rather than having a module
    /// silently install an authentication handler behind its back.
    /// </summary>
    public static IServiceCollection AddCountflowJwtBearer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt configuration section is missing.");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),

                    // Default is five minutes of slack. Warehouse devices sync
                    // time reliably enough that thirty seconds is plenty.
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorization();

        return services;
    }
}
