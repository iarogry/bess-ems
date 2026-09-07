using BatteryEms.Application.Site;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class SiteConfigurationValidatorTests
{
    private readonly SiteConfigurationValidator _validator = new();

    [Fact]
    public void Validate_accepts_site_with_enabled_grid_connection_and_matching_instruments()
    {
        var site = SampleSite();

        var result = _validator.Validate(site);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_rejects_site_without_grid_connection()
    {
        var site = SampleSite() with { GridConnections = [] };

        var result = _validator.Validate(site);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "grid-connections-required");
    }

    [Fact]
    public void Validate_rejects_site_when_all_grid_connections_are_disabled()
    {
        var disabled = SampleGridConnection() with { Enabled = false };
        var site = SampleSite() with { GridConnections = [disabled] };

        var result = _validator.Validate(site);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "grid-connection-enabled-required");
    }

    [Fact]
    public void Validate_rejects_invalid_voltage_limits()
    {
        var invalid = SampleGridConnection() with
        {
            PlannedVoltageV = 10_000,
            MinVoltageV = 10_000,
            MaxVoltageV = 9_999,
        };
        var site = SampleSite() with { GridConnections = [invalid] };

        var result = _validator.Validate(site);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "min-voltage-invalid");
        Assert.Contains(result.Errors, error => error.Code == "max-voltage-invalid");
    }

    [Fact]
    public void Validate_rejects_pv_self_consumption_without_pv_source()
    {
        var site = SampleSite() with { PvSourceRefs = [] };

        var result = _validator.Validate(site);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "pv-self-consumption-requires-pv");
    }

    [Fact]
    public void Validate_rejects_pv_curtailment_without_controllable_pv_source()
    {
        var site = SampleSite() with
        {
            Instruments = new SiteInstrumentSet([SiteInstrument.PvCurtailment]),
        };

        var result = _validator.Validate(site);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "pv-curtailment-requires-control");
    }

    [Fact]
    public void Validate_rejects_grid_export_limit_when_export_is_not_allowed()
    {
        var noExport = SampleGridConnection() with
        {
            ExportAllowed = false,
            MaxExportPowerKw = 0,
        };
        var site = SampleSite() with
        {
            GridConnections = [noExport],
            Instruments = new SiteInstrumentSet([SiteInstrument.GridExportLimit]),
        };

        var result = _validator.Validate(site);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "grid-export-limit-requires-export");
    }

    private static SiteDescriptor SampleSite(string siteId = "site-1") => new(
        SiteId: siteId,
        Name: "Main site",
        TimeZone: "Europe/Kiev",
        MarketBidArea: "10Y1001A1001A869",
        GridConnections: [SampleGridConnection()],
        AssetIds: ["asset-1"],
        PvSourceRefs: [new SiteSourceRef("NE=129469793", "fusionsolar")],
        LoadSourceRefs: [],
        GridMeterSourceRefs: [new SiteSourceRef("grid-meter-1", "meter")],
        Instruments: new SiteInstrumentSet([
            SiteInstrument.BatteryArbitrage,
            SiteInstrument.PvSelfConsumption,
            SiteInstrument.GridImportLimit,
            SiteInstrument.GridExportLimit,
        ]));

    private static GridConnection SampleGridConnection() => new(
        GridConnectionId: "main-10kv",
        Name: "Main 10kV input",
        PlannedVoltageV: 10_000,
        MinVoltageV: 9_000,
        MaxVoltageV: 11_000,
        MaxPhaseImbalancePercent: 5,
        MaxImportPowerKw: 1_000,
        MaxExportPowerKw: 500,
        ExportAllowed: true,
        Enabled: true,
        MeterSourceRefs: [new SiteSourceRef("grid-meter-1", "meter")]);
}
