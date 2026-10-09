using System.ComponentModel.DataAnnotations;

namespace BatteryEms.Adapters.Telecontrol;

public sealed class TelecontrolOptions
{
    public const string SectionName = "Telecontrol";
    public bool Enabled { get; set; }
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
    [Required] public string AssetId { get; set; } = string.Empty;
    public string? SiteId { get; set; }
    [Range(1, int.MaxValue)] public int DeviceId { get; set; }
    [Required] public string SourceTimeZoneId { get; set; } = string.Empty;
    // Until the provider clock convention is confirmed, dashboard values are estimated.
    public bool ClockConfirmed { get; set; }
    public Uri AuthBaseUrl { get; set; } = new("https://kwkac.azurewebsites.net/api/");
    public Uri GatewayBaseUrl { get; set; } = new("https://kwk-gateway-prod.azurewebsites.net/");
    [Range(15, 3600)] public int PollIntervalSeconds { get; set; } = 30;
    [Range(30, 3600)] public int MaxMeasurementAgeSeconds { get; set; } = 120;
    [Range(1, 120)] public int HttpTimeoutSeconds { get; set; } = 30;
}
