using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;

namespace BatteryEms.Adapters.FusionSolar;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFusionSolarSiteTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string? defaultAssetId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<FusionSolarOptions>()
            .Bind(configuration.GetSection(FusionSolarOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.ParsedStationCodes.Count > 0, "FusionSolar:StationCodes is required.")
            .Validate(options => options.ParsedSiteStationCodes.Count == 0
                || (!string.IsNullOrWhiteSpace(options.AssetId)
                    && options.ParsedSiteStationCodes.All(code => options.ParsedStationCodes.Contains(code, StringComparer.OrdinalIgnoreCase))),
                "FusionSolar site membership requires an explicit AssetId and station codes from StationCodes.")
            .ValidateOnStart();

        services.AddHttpClient(
            "FusionSolar",
            (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<FusionSolarOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddTimeProvider();
        services.AddSingleton<FusionSolarSiteTelemetrySource>();
        services.AddHostedService<FusionSolarSiteTelemetryHostedService>();

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
