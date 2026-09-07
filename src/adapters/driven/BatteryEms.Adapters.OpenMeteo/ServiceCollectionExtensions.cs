using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.OpenMeteo;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenMeteoSolarForecast(
        this IServiceCollection services,
        IConfiguration configuration,
        string? defaultAssetId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<OpenMeteoSolarForecastOptions>()
            .Bind(configuration.GetSection(OpenMeteoSolarForecastOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.AssetId) && !string.IsNullOrWhiteSpace(defaultAssetId))
                {
                    options.AssetId = defaultAssetId;
                }
            })
            .ValidateDataAnnotations()
            .Validate(options => options.OutputResolutionMinutes is 15 or 60, "OpenMeteoSolarForecast:OutputResolutionMinutes must be 15 or 60.")
            .Validate(options => options.InstalledDcKw >= options.InverterAcKw, "OpenMeteoSolarForecast:InstalledDcKw must be >= InverterAcKw.")
            .ValidateOnStart();

        services.AddHttpClient(
            "OpenMeteoSolarForecast",
            (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<OpenMeteoSolarForecastOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

        services.TryAddTimeProvider();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<OpenMeteoSolarForecastSource>();
        services.AddSingleton<PvlibSidecarSolarForecastSource>();
        services.AddSingleton<ISolarForecastProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OpenMeteoSolarForecastOptions>>().Value;
            return string.Equals(options.EngineBackend, "embedded", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<OpenMeteoSolarForecastSource>()
                : sp.GetRequiredService<PvlibSidecarSolarForecastSource>();
        });
        services.AddHostedService<OpenMeteoSolarForecastHostedService>();
        return services;
    }

    private static IServiceCollection TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(service => service.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }

        return services;
    }
}
