using BatteryEms.Application.Site;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class SiteBalanceCalculatorTests
{
    [Fact]
    public void Calculate_sums_multiple_grid_inputs_and_subtracts_subconsumers()
    {
        var from = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var result = SiteBalanceCalculator.Calculate(new SiteBalanceCalculationRequest(
            SampleConfiguration(),
            from,
            from.AddHours(1),
            [
                Reading("main-grid-1", "active_energy_import", 100),
                Reading("main-grid-2", "active_energy_import", 25),
                Reading("sub-1", "active_energy_import", 30),
                Reading("solar-1", "active_energy_export", 50),
                Reading("gas-1", "active_energy_import", 3),
                Reading("gas-1", "active_energy_export", 20),
            ],
            []));

        Assert.Equal(125, result.MainGridImportKwh);
        Assert.Equal(30, result.SubconsumerConsumptionKwh);
        Assert.Equal(98, result.OwnConsumptionKwh);
        Assert.Equal(125, result.GridImportKwh);
        Assert.Equal(0, result.GridExportKwh);
        Assert.Equal(125, result.SiteNetBalanceKwh);
        Assert.Equal("complete", result.DataQualityStatus);
    }

    [Fact]
    public void Calculate_does_not_invent_grid_export_from_negative_residual()
    {
        var from = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var result = SiteBalanceCalculator.Calculate(new SiteBalanceCalculationRequest(
            SampleConfiguration(),
            from,
            from.AddHours(1),
            [
                Reading("main-grid-1", "active_energy_import", 0),
                Reading("main-grid-2", "active_energy_import", 0),
                Reading("sub-1", "active_energy_import", 40),
                Reading("main-grid-1", "active_energy_export", 12),
                Reading("solar-1", "active_energy_export", 55),
                Reading("gas-1", "active_energy_export", 20),
            ],
            []));

        Assert.Equal(0, result.OwnConsumptionKwh);
        Assert.Equal(0, result.DerivedExportFromNegativeConsumptionKwh);
        Assert.Contains(result.Warnings, warning => warning.Code == "balance-negative-residual");
        Assert.Equal(12, result.GridExportKwh);
        Assert.Equal(-12, result.SiteNetBalanceKwh);
    }

    [Fact]
    public void Calculate_reports_generation_by_type_separately()
    {
        var from = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var result = SiteBalanceCalculator.Calculate(new SiteBalanceCalculationRequest(
            SampleConfiguration(),
            from,
            from.AddHours(1),
            [
                Reading("main-grid-1", "active_energy_import", 10),
                Reading("main-grid-2", "active_energy_import", 5),
                Reading("sub-1", "active_energy_import", 2),
                Reading("solar-1", "active_energy_export", 50),
                Reading("gas-1", "active_energy_import", 3),
                Reading("gas-1", "active_energy_export", 20),
            ],
            []));

        var gas = Assert.Single(result.Generation, item => item.GenerationType == "gas_cogeneration");
        var solar = Assert.Single(result.Generation, item => item.GenerationType == "solar");
        Assert.Equal(3, gas.AuxiliaryConsumptionKwh);
        Assert.Equal(20, gas.ExportKwh);
        Assert.Equal(17, gas.NetGenerationKwh);
        Assert.Equal(0, solar.AuxiliaryConsumptionKwh);
        Assert.Equal(50, solar.NetGenerationKwh);
    }

    [Fact]
    public void Calculate_marks_result_incomplete_when_required_meter_has_no_reading()
    {
        var from = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var result = SiteBalanceCalculator.Calculate(new SiteBalanceCalculationRequest(
            SampleConfiguration(),
            from,
            from.AddHours(1),
            [
                Reading("main-grid-1", "active_energy_import", 10),
            ],
            []));

        Assert.Equal("incomplete", result.DataQualityStatus);
        Assert.Contains(result.Warnings, warning => warning.Code == "meter-reading-missing" && warning.MeterId == "main-grid-2");
    }

    [Fact]
    public void Calculate_warns_when_fusionsolar_differs_from_askue_solar_by_more_than_tolerance()
    {
        var from = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var result = SiteBalanceCalculator.Calculate(new SiteBalanceCalculationRequest(
            SampleConfiguration(),
            from,
            from.AddHours(1),
            [
                Reading("main-grid-1", "active_energy_import", 10),
                Reading("main-grid-2", "active_energy_import", 5),
                Reading("sub-1", "active_energy_import", 2),
                Reading("solar-1", "active_energy_export", 100),
                Reading("gas-1", "active_energy_export", 20),
            ],
            [
                new SiteMeasurementReading("site-khlibzavod-5", "fusionsolar", "pv", "NE=1", "PV 1", from, TimeSpan.FromHours(1), "pv_energy", 80, "kWh", "measured"),
            ]));

        Assert.Contains(result.Warnings, warning => warning.Code == "generation-crosscheck-mismatch");
    }

    private static SiteBalanceConfiguration SampleConfiguration() => new(
        SiteId: "site-khlibzavod-5",
        CrossCheckTolerancePercent: 5,
        Meters:
        [
            new SiteBalanceMeter("main-grid-1", "Main grid 1", SiteBalanceMeterRole.MainGridMeter, Enabled: true),
            new SiteBalanceMeter("main-grid-2", "Main grid 2", SiteBalanceMeterRole.MainGridMeter, Enabled: true),
            new SiteBalanceMeter("sub-1", "Subconsumer 1", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true),
            new SiteBalanceMeter("solar-1", "Solar generation", SiteBalanceMeterRole.GenerationMeter, Enabled: true, GenerationType: "solar"),
            new SiteBalanceMeter("gas-1", "Gas cogeneration", SiteBalanceMeterRole.GenerationMeter, Enabled: true, GenerationType: "gas_cogeneration"),
        ]);

    private static SiteMeasurementReading Reading(string meterId, string metric, double value) =>
        new("site-khlibzavod-5", "askue", "meter", meterId, meterId, new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), metric, value, "kWh", "measured");
}
