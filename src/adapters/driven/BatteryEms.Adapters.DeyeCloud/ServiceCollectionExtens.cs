using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using BatteryEms.Application.IO;
using System.Net;

namespace BatteryEms.Adapters.DeyeCloud;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeyeCloudTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string? defaultAssetId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<DeyeAdapterOptions>()
            .Bind(configuration.GetSection("DeyeCloud"))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.AssetId)
                    && !string.IsNullOrWhiteSpace(defaultAssetId))
                {
                    options.AssetId = defaultAssetId;
                }
                var siteIds = configuration.GetSection("Dashboard:Sites").GetChildren()
                    .Where(site => site.GetSection("Sources").GetChildren().Any(source =>
                        source["Kind"] == "battery" && source["TelemetryId"] == options.AssetId))
                    .Select(site => site["SiteId"]).Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (siteIds.Length > 1 || (siteIds.Length == 1 && !string.IsNullOrWhiteSpace(options.SiteId) && options.SiteId != siteIds[0]))
                {
                    throw new InvalidOperationException("Deye Cloud asset has conflicting site assignments.");
                }
                if (siteIds.Length == 1) { options.SiteId = siteIds[0]; }
            })
            .ValidateDataAnnotations()
            .Validate(options =>
                !string.IsNullOrWhiteSpace(options.StationId)
                || !string.IsNullOrWhiteSpace(options.DeviceSn),
                "Either DeyeCloud:StationId or DeyeCloud:DeviceSn is required.")
            .Validate(options =>
                !string.IsNullOrWhiteSpace(options.AssetId),
                "DeyeCloud:AssetId is required unless the host can infer a single BESS asset.")
            .ValidateOnStart();

        services.AddHttpClient(
            "DeyeCloud",
            (sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<DeyeAdapterOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });

        services.AddSingleton<IBatteryTelemetrySource, DeyeCloudTelemetrySource>();

        return services;
    }
}
