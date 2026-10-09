using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace BatteryEms.Adapters.DeyeCloud;

public sealed class DeyeAdapterOptions
{
    [Required]
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Bound strictly from appsettings.json text configuration")]
    public string BaseUrl { get; set; } = "https://eu1-developer.deyecloud.com/v1.0";

    [Required]
    public string AppId { get; set; } = string.Empty;

    [Required]
    public string AppSecret { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Internal EMS asset id that snapshots should be recorded under.
    /// </summary>
    public string? AssetId { get; set; }

    public string? SiteId { get; set; }

    /// <summary>
    /// True when Password already contains the SHA-256 hex value expected by Deye Cloud.
    /// </summary>
    public bool PasswordIsSha256 { get; set; }

    /// <summary>
    /// Serial number of the inverter. If provided, the adapter will use /device/latest.
    /// </summary>
    public string? DeviceSn { get; set; }

    /// <summary>
    /// ID of the PV station. If provided, the adapter will use /station/latest.
    /// </summary>
    public string? StationId { get; set; }

    public long? CompanyId { get; set; }

    [Range(0, 3600)]
    public int TokenRefreshLeadSeconds { get; set; } = 300;

    [Range(5, 300)]
    public int HttpTimeoutSeconds { get; set; } = 30;

    [Range(5, 3_600)]
    public int PollIntervalSeconds { get; set; } = 300;

    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    [Range(100, 30_000)]
    public int RetryBaseDelayMs { get; set; } = 500;
}
