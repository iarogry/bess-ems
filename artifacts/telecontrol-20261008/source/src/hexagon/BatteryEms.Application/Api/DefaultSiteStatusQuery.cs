using BatteryEms.Application.Assets;
using BatteryEms.Application.Realtime;

namespace BatteryEms.Application.Api;

public sealed class DefaultSiteStatusQuery : ISiteStatusQuery
{
    private readonly IBatteryAssetRegistry _assets;
    private readonly ISiteTelemetryStore _siteTelemetry;

    public DefaultSiteStatusQuery(
        IBatteryAssetRegistry assets,
        ISiteTelemetryStore siteTelemetry)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(siteTelemetry);
        _assets = assets;
        _siteTelemetry = siteTelemetry;
    }

    public Task<SiteStatusView?> FindAsync(string assetId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        var snapshot = _siteTelemetry.GetLatest(assetId, now);
        if (_assets.Find(assetId) is null && snapshot is null)
        {
            return Task.FromResult<SiteStatusView?>(null);
        }

        return Task.FromResult<SiteStatusView?>(new SiteStatusView(
            AssetId: assetId,
            Telemetry: snapshot?.Telemetry,
            Quality: snapshot?.Quality,
            ObservedAt: snapshot?.ReceivedAt));
    }
}
