using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace BatteryEms.Adapters.FusionSolar;

public sealed class FusionSolarOptions
{
    public const string SectionName = "FusionSolar";

    [Required]
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Bound from operator configuration.")]
    public string BaseUrl { get; set; } = "https://eu5.fusionsolar.huawei.com/thirdData";

    [Required]
    public string User { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string StationCodes { get; set; } = string.Empty;

    public string? AssetId { get; set; }

    // Explicit site membership; the provider account's station list is not a site.
    public string? SiteStationCodes { get; set; }

    public bool UseDeviceTelemetry { get; set; } = true;

    [Range(60, 3600)]
    public int MaxMeasurementAgeSeconds { get; set; } = 600;

    [Range(5, 300)]
    public int HttpTimeoutSeconds { get; set; } = 20;

    [Range(30, 86_400)]
    public int PollIntervalSeconds { get; set; } = 300;

    public IReadOnlyList<string> ParsedStationCodes =>
        StationCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<string> ParsedSiteStationCodes =>
        (SiteStationCodes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
