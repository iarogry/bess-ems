using BatteryEms.Application.Forecasting;

namespace BatteryEms.Application.Api;

public interface ISolarForecastQuery
{
    Task<SolarForecast?> FindAsync(string assetId, CancellationToken cancellationToken);
}
