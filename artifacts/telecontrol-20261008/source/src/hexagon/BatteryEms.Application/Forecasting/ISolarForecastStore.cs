using System.Collections.Concurrent;

namespace BatteryEms.Application.Forecasting;

public interface ISolarForecastStore
{
    void Update(SolarForecast forecast);

    SolarForecast? GetLatest(string assetId);
}

public sealed class InMemorySolarForecastStore : ISolarForecastStore
{
    private readonly ConcurrentDictionary<string, SolarForecast> _forecasts = new(StringComparer.Ordinal);

    public void Update(SolarForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        _forecasts[forecast.AssetId] = forecast;
    }

    public SolarForecast? GetLatest(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        return _forecasts.TryGetValue(assetId, out var forecast) ? forecast : null;
    }
}
