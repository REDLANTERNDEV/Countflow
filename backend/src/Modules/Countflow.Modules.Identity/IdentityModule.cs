using System.Text;
using Countflow.Modules.Identity.Authentication;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.Identity.Features.Authentication;
using Countflow.Modules.Identity.Features.ServiceAccounts;
using Countflow.Modules.Identity.Persistence;
using Countflow.SharedKernel.Modules;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddScoped<ServiceAccountService>();
        services.AddScoped<IModuleSeeder, IdentitySeeder>();

        // TryAdd for the same reason Inventory does it: a clock is host-wide, and a
        // test host may already have substituted a fake.
        services.TryAddSingleton(TimeProvider.System);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        AuthEndpoints.Map(endpoints);
        ServiceAccountEndpoints.Map(endpoints);
    }
}

public static class IdentityModuleExtensions
{
    /// <summary>
    /// Wires authentication for the whole host. Authentication is cross-cutting, so
    /// the host opts in explicitly rather than having a module silently install an
    /// authentication handler behind its back.
    /// <para>
    /// <b>Two credentials, one authorization vocabulary.</b> A person presents a
    /// bearer token and an integration presents <c>X-Api-Key</c>; a policy scheme
    /// picks the handler on the presence of that header, and both handlers produce
    /// the same shape of principal. Every <c>RequireRole</c> already written on
    /// every endpoint therefore covers both, and there is no scope vocabulary
    /// sitting beside the role one waiting to drift out of step with it.
    /// </para>
    /// <para>
    /// The forwarder is what makes that true structurally rather than by
    /// convention. The alternative — accepting both schemes on each endpoint's
    /// authorization policy — would be a line every endpoint author has to
    /// remember, and the one they forget is a public endpoint an integration
    /// silently cannot reach.
    /// </para>
    /// </summary>
    public static IServiceCollection AddCountflowAuthentication(
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
            .AddAuthentication(CountflowAuthenticationSchemes.Selector)
            .AddPolicyScheme(CountflowAuthenticationSchemes.Selector, "Bearer token or API key", options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(CountflowAuthenticationSchemes.ApiKeyHeader)
                        ? CountflowAuthenticationSchemes.ApiKey
                        : JwtBearerDefaults.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                CountflowAuthenticationSchemes.ApiKey, configureOptions: null)
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
