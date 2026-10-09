using BatteryEms.Application.Site;
using System.ComponentModel.DataAnnotations;

namespace BatteryEms.Adapters.OpenMeteo;

public sealed class OpenMeteoSolarForecastOptions
{
    public const string SectionName = "OpenMeteoSolarForecast";

    [Required]
    public string? AssetId { get; set; }

    [Range(-90d, 90d)]
    public double Latitude { get; set; }

    [Range(-180d, 180d)]
    public double Longitude { get; set; }

    [Range(0d, 90d)]
    public double TiltDegrees { get; set; } = 25;

    [Range(-180d, 180d)]
    public double AzimuthDegrees { get; set; } = 0;

    [Range(0.1d, 100000d)]
    public double InstalledDcKw { get; set; }

    [Range(0.1d, 100000d)]
    public double InverterAcKw { get; set; }

    [Range(-0.02d, 0d)]
    public double TemperatureCoefficientPerDegree { get; set; } = -0.004;

    [Range(20d, 80d)]
    public double NoctCellTemperatureCelsius { get; set; } = 45;

    [Range(0d, 1d)]
    public double InverterEfficiency { get; set; } = 0.96;

    [Range(0d, 0.5d)]
    public double SystemLossFraction { get; set; } = 0.14;

    [Range(900, 604800)]
    public int ForecastRefreshSeconds { get; set; } = 1800;

    [Range(1, 168)]
    public int ForecastHours { get; set; } = 48;

    [Range(15, 60)]
    public int OutputResolutionMinutes { get; set; } = 15;

    [Range(5, 300)]
    public int HttpTimeoutSeconds { get; set; } = 30;

    public Uri BaseUrl { get; set; } = new("https://api.open-meteo.com/v1/forecast");

    public string WeatherModel { get; set; } = "best_match";

    public string EngineBackend { get; set; } = "pvlib_sidecar";

    public string PythonExecutable { get; set; } = "python";

    public string SidecarScriptPath { get; set; } = "tools/solar-forecast-engine/pvlib_openmeteo_engine.py";

    public string? SidecarWorkingDirectory { get; set; }

    public OpenMeteoSolarForecastOptions EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(AssetId))
        {
            throw new InvalidOperationException("OpenMeteoSolarForecast:AssetId is required.");
        }
        if (OutputResolutionMinutes is not 15 and not 60)
        {
            throw new InvalidOperationException("OpenMeteoSolarForecast:OutputResolutionMinutes must be 15 or 60.");
        }
        if (InstalledDcKw < InverterAcKw)
        {
            throw new InvalidOperationException("OpenMeteoSolarForecast:InstalledDcKw must be greater than or equal to InverterAcKw.");
        }
        if (!string.Equals(EngineBackend, "embedded", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(EngineBackend, "pvlib_sidecar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("OpenMeteoSolarForecast:EngineBackend must be 'embedded' or 'pvlib_sidecar'.");
        }
        if (string.Equals(EngineBackend, "pvlib_sidecar", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(PythonExecutable))
            {
                throw new InvalidOperationException("OpenMeteoSolarForecast:PythonExecutable is required for pvlib_sidecar.");
            }
            if (string.IsNullOrWhiteSpace(SidecarScriptPath))
            {
                throw new InvalidOperationException("OpenMeteoSolarForecast:SidecarScriptPath is required for pvlib_sidecar.");
            }
        }

        return this;
    }

    public OpenMeteoSolarForecastOptions CreateForSitePvProfile(SitePvProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.EnsureValid();

        return new OpenMeteoSolarForecastOptions
        {
            AssetId = profile.ForecastAssetId,
            Latitude = profile.Latitude,
            Longitude = profile.Longitude,
            TiltDegrees = profile.TiltDegrees,
            AzimuthDegrees = profile.AzimuthDegrees,
            InstalledDcKw = profile.InstalledDcKw,
            InverterAcKw = profile.InverterAcKw,
            TemperatureCoefficientPerDegree = profile.TemperatureCoefficientPerDegree,
            NoctCellTemperatureCelsius = NoctCellTemperatureCelsius,
            InverterEfficiency = InverterEfficiency,
            SystemLossFraction = profile.SystemLossFraction,
            ForecastRefreshSeconds = ForecastRefreshSeconds,
            ForecastHours = profile.ForecastHorizonHours,
            OutputResolutionMinutes = profile.ForecastResolutionMinutes,
            HttpTimeoutSeconds = HttpTimeoutSeconds,
            BaseUrl = BaseUrl,
            WeatherModel = WeatherModel,
            EngineBackend = EngineBackend,
            PythonExecutable = PythonExecutable,
            SidecarScriptPath = SidecarScriptPath,
            SidecarWorkingDirectory = SidecarWorkingDirectory,
        }.EnsureValid();
    }
}
