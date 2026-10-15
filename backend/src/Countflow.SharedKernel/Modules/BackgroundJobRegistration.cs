using Countflow.SharedKernel.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.SharedKernel.Modules;

public sealed record BackgroundJobRegistration(string Name, Type HandlerType, RecurringJobOptions? Schedule = null);

public static class BackgroundJobRegistrationExtensions
{
    public static IServiceCollection AddRecurringJob<T>(this IServiceCollection services,
        string name, IConfiguration configuration, string section,
        Action<RecurringJobOptions>? defaults = null) where T : class, IBackgroundJob
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("A job name must be a stable code of at most 64 ASCII characters.", nameof(name));
        if (services.Any(service => service.ImplementationInstance is BackgroundJobRegistration job && job.Name == name))
            throw new InvalidOperationException($"Background job '{name}' is registered twice.");

        services.AddScoped<T>();
        services.AddSingleton(new BackgroundJobRegistration(name, typeof(T)));
        services.AddOptions<RecurringJobOptions>(name)
            .Configure(options => defaults?.Invoke(options))
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }
}
