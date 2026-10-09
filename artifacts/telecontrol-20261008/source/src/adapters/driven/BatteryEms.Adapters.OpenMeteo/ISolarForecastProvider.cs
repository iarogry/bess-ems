using BatteryEms.Application.Forecasting;

namespace BatteryEms.Adapters.OpenMeteo;

public interface ISolarForecastProvider
{
    Task<SolarForecast> LoadAsync(CancellationToken cancellationToken);

    Task<SolarForecast> LoadAsync(
        OpenMeteoSolarForecastOptions options,
        CancellationToken cancellationToken);
}
