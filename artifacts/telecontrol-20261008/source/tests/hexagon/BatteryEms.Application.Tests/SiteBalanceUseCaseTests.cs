using BatteryEms.Application.Site;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class SiteBalanceUseCaseTests
{
    [Fact]
    public async Task Calculate_reads_persisted_askue_interval_values_and_converts_them_to_energy()
    {
        var from = new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero);
        var store = new InMemorySiteConsumptionStore();
        await store.AppendAsync(
            [
                Raw("870", "Main grid meter 870", from, apoz: 0.80285, aneg: 1.19305),
                Raw("871", "Main grid meter 871", from, apoz: 0, aneg: 0),
                Raw("869", "Subconsumer 869", from, apoz: 1.601),
                Raw("899", "Subconsumer 899", from, apoz: 6.5),
                Raw("900", "Subconsumer 900", from, apoz: 22.39),
                Raw("872", "Subconsumer 872", from, apoz: 0),
                Raw("873", "Subconsumer 873", from, apoz: 0),
                Raw("874", "Subconsumer 874", from, apoz: 0.36585),
                Raw("875", "Subconsumer 875", from, apoz: 0.194),
                Raw("876", "Subconsumer 876", from, apoz: 0.705),
                Raw("929", "Gas cogeneration meter 929", from, apoz: 0.2766, aneg: 24.2829),
            ],
            CancellationToken.None);

        var useCase = new DefaultSiteBalanceUseCase(store);
        var result = await useCase.CalculateAsync(
            new SiteBalanceCommand(Configuration(), from, from.AddDays(1)),
            CancellationToken.None);

        Assert.Equal(321.14, result.MainGridImportKwh, 2);
        Assert.Equal(477.22, result.MainGridExportKwh, 2);
        Assert.Equal(12702.34, result.SubconsumerConsumptionKwh, 2);
        Assert.Equal(0, result.OwnConsumptionKwh, 2);
        Assert.Equal(0, result.DerivedExportFromNegativeConsumptionKwh, 2);
        Assert.Equal(477.22, result.GridExportKwh, 2);
        Assert.Equal(-156.08, result.SiteNetBalanceKwh, 2);

        var generation = Assert.Single(result.Generation);
        Assert.Equal("gas_cogeneration", generation.GenerationType);
        Assert.Equal(110.64, generation.AuxiliaryConsumptionKwh, 2);
        Assert.Equal(9713.16, generation.ExportKwh, 2);
        Assert.Equal(9602.52, generation.NetGenerationKwh, 2);
        Assert.Equal("incomplete", result.DataQualityStatus);
        Assert.Contains(result.Warnings, warning => warning.Code == "meter-window-incomplete");
    }

    private static SiteBalanceConfiguration Configuration() => new(
        SiteId: "site-khlibzavod-5",
        CrossCheckTolerancePercent: 5,
        Meters:
        [
            Meter("870", "Main grid meter 870", SiteBalanceMeterRole.MainGridMeter),
            Meter("871", "Main grid meter 871", SiteBalanceMeterRole.MainGridMeter),
            Meter("869", "Subconsumer 869", SiteBalanceMeterRole.SubconsumerMeter),
            Meter("899", "Subconsumer 899", SiteBalanceMeterRole.SubconsumerMeter),
            Meter("900", "Subconsumer 900", SiteBalanceMeterRole.SubconsumerMeter),
            Meter("872", "Subconsumer 872", SiteBalanceMeterRole.SubconsumerMeter),
            Meter("873", "Subconsumer 873", SiteBalanceMeterRole.SubconsumerMeter),
            Meter("874", "Subconsumer 874", SiteBalanceMeterRole.SubconsumerMeter),
            Meter("875", "Subconsumer 875", SiteBalanceMeterRole.SubconsumerMeter),
            Meter("876", "Subconsumer 876", SiteBalanceMeterRole.SubconsumerMeter),
            Meter("929", "Gas cogeneration meter 929", SiteBalanceMeterRole.GenerationMeter, "gas_cogeneration"),
        ]);

    private static SiteBalanceMeter Meter(
        string meterId,
        string name,
        SiteBalanceMeterRole role,
        string? generationType = null) =>
        new(
            meterId,
            name,
            role,
            Enabled: true,
            GenerationType: generationType,
            ValueMultiplier: 400);

    private static SiteConsumptionReading Raw(
        string pointId,
        string pointName,
        DateTimeOffset timestamp,
        double? apoz = null,
        double? aneg = null) =>
        new(
            "site-khlibzavod-5",
            pointId,
            pointName,
            timestamp,
            apoz,
            aneg,
            null,
            null,
            "askue",
            1800);
}
