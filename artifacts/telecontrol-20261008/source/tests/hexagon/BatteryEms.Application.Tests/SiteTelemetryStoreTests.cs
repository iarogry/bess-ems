using BatteryEms.Application.Realtime;
using BatteryEms.Domain;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class SiteTelemetryStoreTests
{
    [Fact]
    public void GetLatest_returns_site_snapshot_for_current_telemetry()
    {
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromSeconds(10));
        var timestamp = new DateTimeOffset(2026, 6, 4, 9, 0, 0, TimeSpan.Zero);
        var telemetry = new SiteTelemetry(
            Timestamp: timestamp,
            AssetId: "asset-1",
            PvPowerKw: 77.79,
            LoadPowerKw: -18.452,
            GridPowerKw: null,
            IrradianceWPerSquareMeter: 512,
            DataQuality: DataQuality.Valid);

        store.Update(telemetry, timestamp);

        var snapshot = store.GetLatest("asset-1", timestamp.AddSeconds(1));

        Assert.NotNull(snapshot);
        Assert.Equal(DataQualityState.Valid, snapshot!.Quality.Flag);
        Assert.Equal(77.79, snapshot.Telemetry.PvPowerKw);
        Assert.Null(snapshot.Telemetry.GridPowerKw);
    }

    [Fact]
    public void GetLatest_marks_site_snapshot_stale_after_max_age()
    {
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromSeconds(10));
        var timestamp = new DateTimeOffset(2026, 6, 4, 9, 0, 0, TimeSpan.Zero);
        store.Update(
            new SiteTelemetry(timestamp, "asset-1", 5, 3, 1, null, DataQuality.Valid),
            timestamp);

        var snapshot = store.GetLatest("asset-1", timestamp.AddSeconds(11));

        Assert.NotNull(snapshot);
        Assert.Equal(DataQualityState.Stale, snapshot!.Quality.Flag);
        Assert.StartsWith("site-snapshot-aged-", snapshot.Quality.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_marks_non_finite_site_values_as_protocol_error()
    {
        var store = new InMemorySiteTelemetryStore(TimeSpan.FromSeconds(10));
        var timestamp = new DateTimeOffset(2026, 6, 4, 9, 0, 0, TimeSpan.Zero);
        store.Update(
            new SiteTelemetry(timestamp, "asset-1", double.NaN, null, null, null, DataQuality.Valid),
            timestamp);

        var snapshot = store.GetLatest("asset-1", timestamp);

        Assert.NotNull(snapshot);
        Assert.Equal(DataQualityState.ProtocolError, snapshot!.Quality.Flag);
        Assert.Equal("pv-power-not-finite", snapshot.Quality.Reason);
    }
}
