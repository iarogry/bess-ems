using BatteryEms.Application.Assets;
using BatteryEms.Application.Forecasting;

namespace BatteryEms.Application.Api;

public sealed class DefaultSolarForecastQuery : ISolarForecastQuery
{
    private readonly IBatteryAssetRegistry _assets;
    private readonly ISolarForecastStore _forecasts;

    public DefaultSolarForecastQuery(
        IBatteryAssetRegistry assets,
        ISolarForecastStore forecasts)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(forecasts);
        _assets = assets;
        _forecasts = forecasts;
    }

    public Task<SolarForecast?> FindAsync(string assetId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        if (_assets.Find(assetId) is null)
        {
            return Task.FromResult<SolarForecast?>(null);
        }

        return Task.FromResult(_forecasts.GetLatest(assetId));
    }
}
