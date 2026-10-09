namespace BatteryEms.Application.Forecasting;

public sealed class SolarForecast
{
    public string AssetId { get; }
    public string Source { get; }
    public string Model { get; }
    public DateTimeOffset GeneratedAt { get; }
    public DateTimeOffset HorizonStart { get; }
    public DateTimeOffset HorizonEnd { get; }
    public TimeSpan TimeStep { get; }
    public double InstalledDcKw { get; }
    public double InstalledAcKw { get; }
    public IReadOnlyList<SolarForecastPoint> Points { get; }

    public SolarForecast(
        string assetId,
        string source,
        string model,
        DateTimeOffset generatedAt,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        TimeSpan timeStep,
        double installedDcKw,
        double installedAcKw,
        IReadOnlyList<SolarForecastPoint> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(points);
        if (horizonStart >= horizonEnd)
        {
            throw new ArgumentException("HorizonStart must be before HorizonEnd.", nameof(horizonStart));
        }
        if (timeStep <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeStep), "TimeStep must be positive.");
        }
        if (installedDcKw <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(installedDcKw), "InstalledDcKw must be positive.");
        }
        if (installedAcKw <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(installedAcKw), "InstalledAcKw must be positive.");
        }
        if (points.Count == 0)
        {
            throw new ArgumentException("Forecast must contain at least one point.", nameof(points));
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (point.Timestamp < horizonStart || point.Timestamp >= horizonEnd)
            {
                throw new ArgumentException(
                    $"Point timestamp {point.Timestamp:O} is outside the horizon.",
                    nameof(points));
            }

            if (index > 0)
            {
                var expected = points[index - 1].Timestamp + timeStep;
                if (point.Timestamp != expected)
                {
                    throw new ArgumentException(
                        $"Forecast points must be contiguous at {timeStep}. Expected {expected:O}, got {point.Timestamp:O}.",
                        nameof(points));
                }
            }
        }

        AssetId = assetId;
        Source = source;
        Model = model;
        GeneratedAt = generatedAt;
        HorizonStart = horizonStart;
        HorizonEnd = horizonEnd;
        TimeStep = timeStep;
        InstalledDcKw = installedDcKw;
        InstalledAcKw = installedAcKw;
        Points = Array.AsReadOnly(points.ToArray());
    }
}

public sealed record SolarForecastPoint(
    DateTimeOffset Timestamp,
    double PowerKw,
    double IrradianceWPerSquareMeter,
    double AmbientTemperatureCelsius,
    double WindSpeedMetersPerSecond,
    int CloudCoverPercent)
{
    public SolarForecastPoint EnsureValid()
    {
        if (!double.IsFinite(PowerKw) || PowerKw < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PowerKw), "PowerKw must be finite and non-negative.");
        }
        if (!double.IsFinite(IrradianceWPerSquareMeter) || IrradianceWPerSquareMeter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(IrradianceWPerSquareMeter), "Irradiance must be finite and non-negative.");
        }
        if (!double.IsFinite(AmbientTemperatureCelsius))
        {
            throw new ArgumentOutOfRangeException(nameof(AmbientTemperatureCelsius), "AmbientTemperatureCelsius must be finite.");
        }
        if (!double.IsFinite(WindSpeedMetersPerSecond) || WindSpeedMetersPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WindSpeedMetersPerSecond), "Wind speed must be finite and non-negative.");
        }
        if (CloudCoverPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(CloudCoverPercent), "CloudCoverPercent must be between 0 and 100.");
        }

        return this;
    }
}
