using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryEms.Application.Forecasting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.OpenMeteo;

internal sealed partial class PvlibSidecarSolarForecastSource : ISolarForecastProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly OpenMeteoSolarForecastOptions _options;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<PvlibSidecarSolarForecastSource> _logger;

    public PvlibSidecarSolarForecastSource(
        IOptions<OpenMeteoSolarForecastOptions> options,
        IProcessRunner processRunner,
        ILogger<PvlibSidecarSolarForecastSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _processRunner = processRunner;
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

        var payloadJson = JsonSerializer.Serialize(
            BuildSidecarRequest(options),
            JsonOptions);
        var request = new ProcessRunRequest(
            FileName: options.PythonExecutable,
            Arguments:
            [
                options.SidecarScriptPath,
                "--stdin-json",
            ],
            StandardInput: payloadJson,
            WorkingDirectory: options.SidecarWorkingDirectory);
        var result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pvlib-sidecar-failed exit_code={result.ExitCode} stderr={result.StandardError.Trim()}");
        }

        var response = JsonSerializer.Deserialize<PvlibSidecarResponse>(result.StandardOutput, JsonOptions)
            ?? throw new InvalidOperationException("pvlib-sidecar returned an empty response.");
        var forecast = ToSolarForecast(response);
        LogForecastLoaded(forecast.AssetId, forecast.Points.Count, forecast.HorizonStart, forecast.HorizonEnd);
        return forecast;
    }

    internal static object BuildSidecarRequest(OpenMeteoSolarForecastOptions options) => new
    {
        asset_id = options.AssetId,
        latitude = options.Latitude,
        longitude = options.Longitude,
        tilt_degrees = options.TiltDegrees,
        azimuth_degrees = options.AzimuthDegrees,
        installed_dc_kw = options.InstalledDcKw,
        inverter_ac_kw = options.InverterAcKw,
        temperature_coefficient_per_degree = options.TemperatureCoefficientPerDegree,
        system_loss_fraction = options.SystemLossFraction,
        forecast_hours = options.ForecastHours,
        output_resolution_minutes = options.OutputResolutionMinutes,
        base_url = options.BaseUrl.ToString(),
        weather_model = options.WeatherModel,
        http_timeout_seconds = options.HttpTimeoutSeconds,
    };

    internal static SolarForecast ToSolarForecast(PvlibSidecarResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.AssetId))
        {
            throw new InvalidOperationException("pvlib-sidecar response is missing asset_id.");
        }
        if (response.Points.Count == 0)
        {
            throw new InvalidOperationException("pvlib-sidecar response contains no forecast points.");
        }

        return new SolarForecast(
            assetId: response.AssetId,
            source: response.Source,
            model: response.Model,
            generatedAt: ParseUtc(response.GeneratedAt),
            horizonStart: ParseUtc(response.HorizonStart),
            horizonEnd: ParseUtc(response.HorizonEnd),
            timeStep: TimeSpan.FromSeconds(response.TimeStepSeconds),
            installedDcKw: response.InstalledDcKw,
            installedAcKw: response.InstalledAcKw,
            points: response.Points.Select(point => new SolarForecastPoint(
                Timestamp: ParseUtc(point.Timestamp),
                PowerKw: point.PowerKw,
                IrradianceWPerSquareMeter: point.IrradianceWPerSquareMeter,
                AmbientTemperatureCelsius: point.AmbientTemperatureCelsius,
                WindSpeedMetersPerSecond: point.WindSpeedMetersPerSecond,
                CloudCoverPercent: point.CloudCoverPercent).EnsureValid()).ToArray());
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "pvlib sidecar forecast refreshed for asset '{AssetId}'. Points={PointCount}, Horizon={HorizonStart:O}..{HorizonEnd:O}.")]
    private partial void LogForecastLoaded(
        string assetId,
        int pointCount,
        DateTimeOffset horizonStart,
        DateTimeOffset horizonEnd);
}

internal sealed class PvlibSidecarResponse
{
    [JsonPropertyName("asset_id")]
    public string AssetId { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; init; } = string.Empty;

    [JsonPropertyName("horizon_start")]
    public string HorizonStart { get; init; } = string.Empty;

    [JsonPropertyName("horizon_end")]
    public string HorizonEnd { get; init; } = string.Empty;

    [JsonPropertyName("time_step_seconds")]
    public double TimeStepSeconds { get; init; }

    [JsonPropertyName("installed_dc_kw")]
    public double InstalledDcKw { get; init; }

    [JsonPropertyName("installed_ac_kw")]
    public double InstalledAcKw { get; init; }

    [JsonPropertyName("points")]
    public IReadOnlyList<PvlibSidecarPoint> Points { get; init; } = [];
}

internal sealed class PvlibSidecarPoint
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = string.Empty;

    [JsonPropertyName("power_kw")]
    public double PowerKw { get; init; }

    [JsonPropertyName("irradiance_w_per_square_meter")]
    public double IrradianceWPerSquareMeter { get; init; }

    [JsonPropertyName("ambient_temperature_celsius")]
    public double AmbientTemperatureCelsius { get; init; }

    [JsonPropertyName("wind_speed_meters_per_second")]
    public double WindSpeedMetersPerSecond { get; init; }

    [JsonPropertyName("cloud_cover_percent")]
    public int CloudCoverPercent { get; init; }
}
