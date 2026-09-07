using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BatteryEms.Application.Forecasting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.OpenMeteo;

public sealed partial class OpenMeteoSolarForecastSource : ISolarForecastProvider
{
    internal const string SourceId = "open-meteo";
    internal const string ModelId = "open-meteo-gti-pvwatts-lite";
    private const string HttpClientName = "OpenMeteoSolarForecast";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenMeteoSolarForecastOptions _options;
    private readonly ILogger<OpenMeteoSolarForecastSource> _logger;

    public OpenMeteoSolarForecastSource(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenMeteoSolarForecastOptions> options,
        ILogger<OpenMeteoSolarForecastSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public Task<SolarForecast> LoadAsync(CancellationToken cancellationToken) =>
        LoadAsync(_options, cancellationToken);

    public async Task<SolarForecast> LoadAsync(
        OpenMeteoSolarForecastOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options.EnsureValid();

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var response = await client.GetFromJsonAsync<OpenMeteoForecastResponse>(
            BuildRequestUri(options),
            cancellationToken).ConfigureAwait(false);

        if (response?.Hourly is null)
        {
            throw new InvalidOperationException("Open-Meteo returned an empty hourly payload.");
        }

        var hourlyPoints = BuildHourlyPoints(options, response.Hourly);
        if (hourlyPoints.Count < 2)
        {
            throw new InvalidOperationException("Open-Meteo returned insufficient hourly points for interpolation.");
        }

        var timeStep = TimeSpan.FromMinutes(options.OutputResolutionMinutes);
        var generatedAt = DateTimeOffset.UtcNow;
        var resampled = Resample(hourlyPoints, timeStep);
        var horizonStart = resampled[0].Timestamp;
        var horizonEnd = resampled[^1].Timestamp + timeStep;

        LogForecastLoaded(
            options.AssetId!,
            resampled.Count,
            horizonStart,
            horizonEnd,
            options.OutputResolutionMinutes);

        return new SolarForecast(
            assetId: options.AssetId!,
            source: SourceId,
            model: ModelId,
            generatedAt: generatedAt,
            horizonStart: horizonStart,
            horizonEnd: horizonEnd,
            timeStep: timeStep,
            installedDcKw: options.InstalledDcKw,
            installedAcKw: options.InverterAcKw,
            points: resampled);
    }

    private static Uri BuildRequestUri(OpenMeteoSolarForecastOptions options)
    {
        var parameters = new Dictionary<string, string>
        {
            ["latitude"] = options.Latitude.ToString(CultureInfo.InvariantCulture),
            ["longitude"] = options.Longitude.ToString(CultureInfo.InvariantCulture),
            ["timezone"] = "UTC",
            ["models"] = options.WeatherModel,
            ["forecast_hours"] = options.ForecastHours.ToString(CultureInfo.InvariantCulture),
            ["tilt"] = options.TiltDegrees.ToString(CultureInfo.InvariantCulture),
            ["azimuth"] = options.AzimuthDegrees.ToString(CultureInfo.InvariantCulture),
            ["hourly"] = string.Join(
                ",",
                "global_tilted_irradiance",
                "temperature_2m",
                "wind_speed_10m",
                "cloud_cover"),
        };

        var builder = new UriBuilder(options.BaseUrl)
        {
            Query = string.Join("&", parameters.Select(
                pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    internal IReadOnlyList<SolarForecastPoint> BuildHourlyPoints(OpenMeteoHourly hourly) =>
        BuildHourlyPoints(_options.EnsureValid(), hourly);

    internal static IReadOnlyList<SolarForecastPoint> BuildHourlyPoints(
        OpenMeteoSolarForecastOptions options,
        OpenMeteoHourly hourly)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureAligned(hourly);

        var points = new List<SolarForecastPoint>(hourly.Time.Count);
        for (var index = 0; index < hourly.Time.Count; index++)
        {
            var timestamp = ParseTimestamp(hourly.Time[index]);
            var irradiance = Math.Max(0d, hourly.GlobalTiltedIrradiance[index]);
            var ambientTemp = hourly.Temperature2m[index];
            var windSpeed = Math.Max(0d, hourly.WindSpeed10m[index]);
            var cloudCover = ClampCloudCover(hourly.CloudCover[index]);
            var powerKw = EstimatePowerKw(options, irradiance, ambientTemp, windSpeed);

            points.Add(new SolarForecastPoint(
                Timestamp: timestamp,
                PowerKw: powerKw,
                IrradianceWPerSquareMeter: irradiance,
                AmbientTemperatureCelsius: ambientTemp,
                WindSpeedMetersPerSecond: windSpeed,
                CloudCoverPercent: cloudCover).EnsureValid());
        }

        return points;
    }

    internal double EstimatePowerKw(
        double irradianceWPerSquareMeter,
        double ambientTemperatureCelsius,
        double windSpeedMetersPerSecond) =>
        EstimatePowerKw(_options.EnsureValid(), irradianceWPerSquareMeter, ambientTemperatureCelsius, windSpeedMetersPerSecond);

    internal static double EstimatePowerKw(
        OpenMeteoSolarForecastOptions options,
        double irradianceWPerSquareMeter,
        double ambientTemperatureCelsius,
        double windSpeedMetersPerSecond)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(irradianceWPerSquareMeter) || irradianceWPerSquareMeter <= 0)
        {
            return 0;
        }

        var adjustedNoct = Math.Max(30d, options.NoctCellTemperatureCelsius - Math.Min(10d, windSpeedMetersPerSecond * 0.8d));
        var cellTemperature = ambientTemperatureCelsius
            + ((adjustedNoct - 20d) / 800d) * irradianceWPerSquareMeter;
        var temperatureFactor = 1d
            + options.TemperatureCoefficientPerDegree * (cellTemperature - 25d);
        var dcKw = options.InstalledDcKw
            * (irradianceWPerSquareMeter / 1000d)
            * Math.Max(0d, temperatureFactor);
        var netAcKw = dcKw * options.InverterEfficiency * (1d - options.SystemLossFraction);
        return Math.Round(Math.Clamp(netAcKw, 0d, options.InverterAcKw), 3, MidpointRounding.AwayFromZero);
    }

    internal static IReadOnlyList<SolarForecastPoint> Resample(
        IReadOnlyList<SolarForecastPoint> hourlyPoints,
        TimeSpan timeStep)
    {
        ArgumentNullException.ThrowIfNull(hourlyPoints);
        if (hourlyPoints.Count < 2)
        {
            throw new ArgumentException("At least two hourly points are required.", nameof(hourlyPoints));
        }
        if (timeStep <= TimeSpan.Zero || TimeSpan.FromHours(1).Ticks % timeStep.Ticks != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeStep), "Time step must evenly divide one hour.");
        }

        if (timeStep == TimeSpan.FromHours(1))
        {
            return hourlyPoints.Select(point => point.EnsureValid()).ToArray();
        }

        var output = new List<SolarForecastPoint>();
        for (var index = 0; index < hourlyPoints.Count - 1; index++)
        {
            var start = hourlyPoints[index];
            var end = hourlyPoints[index + 1];
            var steps = (int)(TimeSpan.FromHours(1).Ticks / timeStep.Ticks);

            for (var step = 0; step < steps; step++)
            {
                var ratio = step / (double)steps;
                output.Add(new SolarForecastPoint(
                    Timestamp: start.Timestamp + TimeSpan.FromTicks(timeStep.Ticks * step),
                    PowerKw: Interpolate(start.PowerKw, end.PowerKw, ratio),
                    IrradianceWPerSquareMeter: Interpolate(start.IrradianceWPerSquareMeter, end.IrradianceWPerSquareMeter, ratio),
                    AmbientTemperatureCelsius: Interpolate(start.AmbientTemperatureCelsius, end.AmbientTemperatureCelsius, ratio),
                    WindSpeedMetersPerSecond: Interpolate(start.WindSpeedMetersPerSecond, end.WindSpeedMetersPerSecond, ratio),
                    CloudCoverPercent: ClampCloudCover((int)Math.Round(Interpolate(start.CloudCoverPercent, end.CloudCoverPercent, ratio), MidpointRounding.AwayFromZero)))
                    .EnsureValid());
            }
        }

        var finalPoint = hourlyPoints[^1];
        output.Add(new SolarForecastPoint(
            Timestamp: hourlyPoints[0].Timestamp + TimeSpan.FromTicks(timeStep.Ticks * (output.Count)),
            PowerKw: finalPoint.PowerKw,
            IrradianceWPerSquareMeter: finalPoint.IrradianceWPerSquareMeter,
            AmbientTemperatureCelsius: finalPoint.AmbientTemperatureCelsius,
            WindSpeedMetersPerSecond: finalPoint.WindSpeedMetersPerSecond,
            CloudCoverPercent: finalPoint.CloudCoverPercent).EnsureValid());
        return output;
    }

    private static double Interpolate(double left, double right, double ratio) =>
        left + ((right - left) * ratio);

    private static int ClampCloudCover(int value) => Math.Clamp(value, 0, 100);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void EnsureAligned(OpenMeteoHourly hourly)
    {
        var lengths = new[]
        {
            hourly.Time.Count,
            hourly.GlobalTiltedIrradiance.Count,
            hourly.Temperature2m.Count,
            hourly.WindSpeed10m.Count,
            hourly.CloudCover.Count,
        };

        if (lengths.Any(length => length != lengths[0]))
        {
            throw new InvalidOperationException("Open-Meteo hourly arrays are not aligned.");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Solar forecast refreshed for asset '{AssetId}'. Points={PointCount}, Horizon={HorizonStart:O}..{HorizonEnd:O}, StepMinutes={StepMinutes}.")]
    private partial void LogForecastLoaded(
        string assetId,
        int pointCount,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd,
        int stepMinutes);
}

public sealed class OpenMeteoForecastResponse
{
    [JsonPropertyName("hourly")]
    public OpenMeteoHourly? Hourly { get; set; }
}

public sealed class OpenMeteoHourly
{
    [JsonPropertyName("time")]
    public IReadOnlyList<string> Time { get; init; } = [];

    [JsonPropertyName("global_tilted_irradiance")]
    public IReadOnlyList<double> GlobalTiltedIrradiance { get; init; } = [];

    [JsonPropertyName("temperature_2m")]
    public IReadOnlyList<double> Temperature2m { get; init; } = [];

    [JsonPropertyName("wind_speed_10m")]
    public IReadOnlyList<double> WindSpeed10m { get; init; } = [];

    [JsonPropertyName("cloud_cover")]
    public IReadOnlyList<int> CloudCover { get; init; } = [];
}
