namespace BatteryEms.Application.Realtime;

public interface ISiteTelemetryStore
{
    void Update(SiteTelemetry telemetry, DateTimeOffset receivedAt);

    SiteSnapshot? GetLatest(string assetId, DateTimeOffset now);
}
