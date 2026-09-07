using BatteryEms.Application.Site;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class InMemorySiteRegistryTests
{
    [Fact]
    public void Find_returns_site_by_site_id()
    {
        var siteA = SampleSite("site-a", "asset-a");
        var siteB = SampleSite("site-b", "asset-b");
        var registry = new InMemorySiteRegistry([siteA, siteB]);

        var found = registry.Find("site-b");

        Assert.NotNull(found);
        Assert.Equal("site-b", found!.SiteId);
        Assert.Equal("asset-b", Assert.Single(found.AssetIds));
    }

    [Fact]
    public void All_returns_sites_ordered_by_site_id()
    {
        var registry = new InMemorySiteRegistry([
            SampleSite("site-b", "asset-b"),
            SampleSite("site-a", "asset-a"),
        ]);

        var sites = registry.All();

        Assert.Collection(
            sites,
            site => Assert.Equal("site-a", site.SiteId),
            site => Assert.Equal("site-b", site.SiteId));
    }

    [Fact]
    public void Register_replaces_existing_site_without_affecting_other_sites()
    {
        var registry = new InMemorySiteRegistry([
            SampleSite("site-a", "asset-a"),
            SampleSite("site-b", "asset-b"),
        ]);

        registry.Register(SampleSite("site-a", "asset-a2"));

        Assert.Equal("asset-a2", Assert.Single(registry.Find("site-a")!.AssetIds));
        Assert.Equal("asset-b", Assert.Single(registry.Find("site-b")!.AssetIds));
    }

    private static SiteDescriptor SampleSite(string siteId, string assetId) => new(
        SiteId: siteId,
        Name: siteId,
        TimeZone: "Europe/Kiev",
        MarketBidArea: "10Y1001A1001A869",
        GridConnections: [
            new GridConnection(
                GridConnectionId: siteId + "-grid",
                Name: "Grid",
                PlannedVoltageV: 400,
                MinVoltageV: 360,
                MaxVoltageV: 440,
                MaxPhaseImbalancePercent: 5,
                MaxImportPowerKw: 100,
                MaxExportPowerKw: 50,
                ExportAllowed: true,
                Enabled: true,
                MeterSourceRefs: [])
        ],
        AssetIds: [assetId],
        PvSourceRefs: [],
        LoadSourceRefs: [],
        GridMeterSourceRefs: [],
        Instruments: new SiteInstrumentSet([SiteInstrument.BatteryArbitrage]));
}
