using BatteryEms.Application.Realtime;
using BatteryEms.Domain;

namespace BatteryEms.Application.Api;

public interface ISiteStatusQuery
{
    Task<SiteStatusView?> FindAsync(string assetId, DateTimeOffset now, CancellationToken cancellationToken);
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteStatusView(
    string AssetId,
    SiteTelemetry? Telemetry,
    DataQuality? Quality,
    DateTimeOffset? ObservedAt);
