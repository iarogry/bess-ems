using BatteryEms.Application.Site;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class InMemorySitePvProfileStoreTests
{
    [Fact]
    public async Task Upsert_and_find_round_trip_profile()
    {
        var store = new InMemorySitePvProfileStore();
        var profile = SampleProfile();

        await store.UpsertAsync(profile, CancellationToken.None);

        var loaded = await store.FindAsync(profile.SiteId, profile.PvSystemId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(profile.ForecastAssetId, loaded!.ForecastAssetId);
        Assert.Equal(profile.InstalledDcKw, loaded.InstalledDcKw);
    }

    [Fact]
    public async Task ListEnabled_returns_only_enabled_profiles()
    {
        var store = new InMemorySitePvProfileStore();
        await store.UpsertAsync(SampleProfile(), CancellationToken.None);
        await store.UpsertAsync(SampleProfile(pvSystemId: "pv-2", forecastAssetId: "forecast-2", enabled: false), CancellationToken.None);

        var enabled = await store.ListEnabledAsync(CancellationToken.None);

        Assert.Single(enabled);
        Assert.Equal("pv-1", enabled[0].PvSystemId);
    }

    private static SitePvProfile SampleProfile(
        string siteId = "site-1",
        string pvSystemId = "pv-1",
        string forecastAssetId = "forecast-1",
        bool enabled = true) => new(
            SiteId: siteId,
            PvSystemId: pvSystemId,
            Name: "Roof PV",
            ForecastAssetId: forecastAssetId,
            Enabled: enabled,
            Latitude: 50.45,
            Longitude: 30.52,
            TiltDegrees: 25,
            AzimuthDegrees: 0,
            InstalledDcKw: 500,
            InverterAcKw: 450,
            TemperatureCoefficientPerDegree: -0.004,
            SystemLossFraction: 0.14,
            ForecastHorizonHours: 48,
            ForecastResolutionMinutes: 15,
            ForecastProvider: "open_meteo",
            ForecastEngine: "pvlib_sidecar");
}
