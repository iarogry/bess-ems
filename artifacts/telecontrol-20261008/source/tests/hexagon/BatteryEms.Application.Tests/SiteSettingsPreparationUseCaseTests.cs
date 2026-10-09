using BatteryEms.Application.Realtime;
using BatteryEms.Application.Site;
using BatteryEms.Domain;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class SiteSettingsPreparationUseCaseTests
{
    [Fact]
    public async Task PrepareAsync_normalizes_raw_measurements_into_site_prepared_values()
    {
        var from = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var site = SampleSite();
        var measurements = new InMemorySiteMeasurementStore();
        await measurements.AppendAsync([
            new SiteMeasurementReading("site-1", "fusionsolar", "pv", "NE=129469793", "PV", from, TimeSpan.FromHours(1), "pv_energy", 12, "kWh", "measured"),
            new SiteMeasurementReading("site-1", "askue", "meter", "grid-meter-1", "Grid", from, TimeSpan.FromHours(1), "active_energy_import", 20, "kWh", "measured"),
            new SiteMeasurementReading("site-1", "askue", "meter", "grid-meter-1", "Grid", from, null, "phase_l1_voltage", 10_020, "V", "measured"),
            new SiteMeasurementReading("site-1", "askue", "meter", "grid-meter-1", "Grid", from, null, "phase_l2_voltage", 10_000, "V", "measured"),
            new SiteMeasurementReading("site-1", "askue", "meter", "grid-meter-1", "Grid", from, null, "phase_l3_voltage", 9_980, "V", "measured"),
        ], CancellationToken.None);
        var useCase = CreateUseCase(site, measurements);

        var result = await useCase.PrepareAsync(
            new SiteSettingsPreparationCommand("site-1", from, from.AddHours(1), TimeSpan.FromHours(1)),
            CancellationToken.None);

        var step = Assert.Single(result.Steps);
        Assert.Equal(12, step.PvEnergyKwh);
        Assert.Equal(12, step.PvPowerKw);
        Assert.Equal(20, step.LoadEnergyKwh);
        Assert.Equal(20, step.LoadPowerKw);
        Assert.Equal(20, step.GridImportEnergyKwh);
        Assert.Equal(20, step.GridImportPowerKw);
        Assert.Equal(DataQuality.Valid, step.DataQuality);
        Assert.False(Assert.Single(step.GridVoltageHealth).RequiresIslandTransition);
    }

    [Fact]
    public async Task PrepareAsync_applies_effective_grid_limits()
    {
        var from = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var site = SampleSite() with
        {
            GridConnections =
            [
                SampleGridConnection() with
                {
                    ExportAllowed = false,
                    MaxExportPowerKw = 500,
                },
                SampleGridConnection("disabled") with
                {
                    Enabled = false,
                    MaxImportPowerKw = 100,
                    MaxExportPowerKw = 100,
                },
            ],
        };
        var useCase = CreateUseCase(site, new InMemorySiteMeasurementStore());

        var result = await useCase.PrepareAsync(
            new SiteSettingsPreparationCommand("site-1", from, from.AddHours(1), TimeSpan.FromHours(1)),
            CancellationToken.None);

        Assert.Equal(2, result.GridConnections.Count);
        Assert.Equal(1_000, result.GridConnections[0].EffectiveMaxImportPowerKw);
        Assert.Equal(0, result.GridConnections[0].EffectiveMaxExportPowerKw);
        Assert.Equal(0, result.GridConnections[1].EffectiveMaxImportPowerKw);
        Assert.Equal(0, result.GridConnections[1].EffectiveMaxExportPowerKw);
    }

    [Fact]
    public async Task PrepareAsync_marks_voltage_out_of_range_as_island_transition_intent()
    {
        var from = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var measurements = new InMemorySiteMeasurementStore();
        await measurements.AppendAsync([
            new SiteMeasurementReading("site-1", "askue", "meter", "grid-meter-1", "Grid", from, null, "phase_l1_voltage", 8_800, "V", "measured"),
            new SiteMeasurementReading("site-1", "askue", "meter", "grid-meter-1", "Grid", from, null, "phase_l2_voltage", 10_000, "V", "measured"),
            new SiteMeasurementReading("site-1", "askue", "meter", "grid-meter-1", "Grid", from, null, "phase_l3_voltage", 10_000, "V", "measured"),
        ], CancellationToken.None);
        var useCase = CreateUseCase(SampleSite(), measurements);

        var result = await useCase.PrepareAsync(
            new SiteSettingsPreparationCommand("site-1", from, from.AddHours(1), TimeSpan.FromHours(1)),
            CancellationToken.None);

        var health = Assert.Single(Assert.Single(result.Steps).GridVoltageHealth);
        Assert.False(health.IsWithinVoltageLimits);
        Assert.True(health.RequiresIslandTransition);
        Assert.Equal(DataQuality.ProtocolError("grid-voltage-unsafe"), Assert.Single(result.Steps).DataQuality);
    }

    [Fact]
    public async Task PrepareAsync_uses_realtime_pv_snapshot_when_measurement_is_missing()
    {
        var from = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var telemetry = new InMemorySiteTelemetryStore(TimeSpan.FromHours(2));
        telemetry.Update(
            new SiteTelemetry(
                from,
                "NE=129469793",
                PvPowerKw: 8,
                LoadPowerKw: null,
                GridPowerKw: null,
                IrradianceWPerSquareMeter: null,
                DataQuality.Valid),
            from);
        var useCase = CreateUseCase(
            SampleSite(),
            new InMemorySiteMeasurementStore(),
            telemetry: telemetry);

        var result = await useCase.PrepareAsync(
            new SiteSettingsPreparationCommand("site-1", from, from.AddHours(1), TimeSpan.FromHours(1)),
            CancellationToken.None);

        var step = Assert.Single(result.Steps);
        Assert.Equal(8, step.PvPowerKw);
        Assert.Equal(8, step.PvEnergyKwh);
    }

    [Fact]
    public async Task PrepareAsync_accepts_canonical_inverter_yield_metric()
    {
        var from = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var measurements = new InMemorySiteMeasurementStore();
        await measurements.AppendAsync([
            new SiteMeasurementReading("site-1", "fusionsolar", "pv", "NE=129469793", "PV", from, TimeSpan.FromHours(1), "inverter_yield", 7.5, "kWh", "measured"),
        ], CancellationToken.None);
        var useCase = CreateUseCase(SampleSite(), measurements);

        var result = await useCase.PrepareAsync(
            new SiteSettingsPreparationCommand("site-1", from, from.AddHours(1), TimeSpan.FromHours(1)),
            CancellationToken.None);

        var step = Assert.Single(result.Steps);
        Assert.Equal(7.5, step.PvEnergyKwh);
        Assert.Equal(7.5, step.PvPowerKw);
    }

    [Fact]
    public async Task PrepareAsync_ignores_status_only_missing_measurement_values()
    {
        var from = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var measurements = new InMemorySiteMeasurementStore();
        await measurements.AppendAsync([
            new SiteMeasurementReading("site-1", "fusionsolar", "pv", "NE=129469793", "PV", from, TimeSpan.FromHours(1), "pv_energy", null, "kWh", "missing"),
        ], CancellationToken.None);
        var useCase = CreateUseCase(SampleSite(), measurements);

        var result = await useCase.PrepareAsync(
            new SiteSettingsPreparationCommand("site-1", from, from.AddHours(1), TimeSpan.FromHours(1)),
            CancellationToken.None);

        var step = Assert.Single(result.Steps);
        Assert.Null(step.PvEnergyKwh);
        Assert.Null(step.PvPowerKw);
        Assert.Equal(DataQuality.Stale("site-prepared-values-missing"), step.DataQuality);
    }

    private static DefaultSiteSettingsPreparationUseCase CreateUseCase(
        SiteDescriptor site,
        ISiteMeasurementStore measurements,
        ISiteConsumptionStore? consumption = null,
        ISiteTelemetryStore? telemetry = null) =>
        new(
            new InMemorySiteRegistry([site]),
            measurements,
            consumption ?? new InMemorySiteConsumptionStore(),
            telemetry ?? new InMemorySiteTelemetryStore(TimeSpan.FromHours(2)));

    private static SiteDescriptor SampleSite() => new(
        SiteId: "site-1",
        Name: "Main site",
        TimeZone: "Europe/Kiev",
        MarketBidArea: "10Y1001A1001A869",
        GridConnections: [SampleGridConnection()],
        AssetIds: ["single-bess-1"],
        PvSourceRefs: [new SiteSourceRef("NE=129469793", "fusionsolar")],
        LoadSourceRefs: [new SiteSourceRef("grid-meter-1", "askue")],
        GridMeterSourceRefs: [new SiteSourceRef("grid-meter-1", "askue")],
        Instruments: new SiteInstrumentSet([
            SiteInstrument.BatteryArbitrage,
            SiteInstrument.PvSelfConsumption,
            SiteInstrument.GridImportLimit,
            SiteInstrument.GridExportLimit,
        ]));

    private static GridConnection SampleGridConnection(string id = "main-10kv") => new(
        GridConnectionId: id,
        Name: "Main 10kV input",
        PlannedVoltageV: 10_000,
        MinVoltageV: 9_000,
        MaxVoltageV: 11_000,
        MaxPhaseImbalancePercent: 5,
        MaxImportPowerKw: 1_000,
        MaxExportPowerKw: 500,
        ExportAllowed: true,
        Enabled: true,
        MeterSourceRefs: [new SiteSourceRef("grid-meter-1", "askue")]);
}
