using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace BatteryEms.Adapters.Askue;

public sealed class AskueOptions
{
    public const string SectionName = "Askue";

    [Required]
    public string SiteId { get; set; } = string.Empty;

    [Required]
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Bound from operator configuration.")]
    public string BaseUrl { get; set; } = "http://askue.net/api/askue/v1/json/";

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public string? PointIds { get; set; }

    [Required]
    public string TimeZoneId { get; set; } = "Europe/Kyiv";

    [Range(300, 86_400)]
    public int PeriodSeconds { get; set; } = 1800;

    [Range(5, 300)]
    public int HttpTimeoutSeconds { get; set; } = 30;

    [Range(60, 86_400)]
    public int PollIntervalSeconds { get; set; } = 3600;

    [Range(0, 30)]
    public int DaysBack { get; set; } = 1;

    public IReadOnlyList<string> ParsedPointIds =>
        string.IsNullOrWhiteSpace(PointIds)
            ? []
            : PointIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
