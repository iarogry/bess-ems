using BatteryEms.Adapters.OpenMeteo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BatteryEms.Host;

internal static class SolarForecastRegistration
{
    public static void Configure(
        IServiceCollection services,
        BessHostOptions hostOptions,
        BessRuntimeConfiguration runtimeConfig,
        ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(runtimeConfig);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(hostOptions.SolarForecastSource))
        {
            return;
        }

        if (string.Equals(hostOptions.SolarForecastSource, "open_meteo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostOptions.SolarForecastSource, "open-meteo", StringComparison.OrdinalIgnoreCase))
        {
            services.AddOpenMeteoSolarForecast(
                configuration,
                runtimeConfig.Assets.Count == 1 ? runtimeConfig.SingleAsset.AssetId : null);
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Bess:SolarForecastSource '{hostOptions.SolarForecastSource}'. Supported values: open_meteo.");
    }
}
