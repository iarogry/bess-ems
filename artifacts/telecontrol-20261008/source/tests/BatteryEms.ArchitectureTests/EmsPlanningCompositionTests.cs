using System.Text.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;
using BatteryEms.Host;
using BatteryEms.Host.Planning;
using BatteryEms.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.ArchitectureTests;

public sealed class EmsPlanningCompositionTests
{
    [Fact]
    public async Task Host_binds_equipment_settings_and_Deye_respects_the_asset_SOC_ceiling()
    {
        await using var app = BessHostBuilder.BuildApp(Arguments());
        var options = app.Services.GetRequiredService<EmsPlanningOptions>();
        var target = Assert.Single(options.Targets);
        Assert.Equal(30, target.ReserveSocPercent);
        Assert.Equal("deye_cloud", target.IntegrationId);
        Assert.NotNull(app.Services.GetRequiredService<EmsPlanningHostedService>());
        Assert.IsType<FailClosedActivationPlanDispatcher>(app.Services.GetRequiredService<IActivationPlanDispatcher>());
        var date = new DateOnly(2026, 10, 8);
        var horizon = EmsPlanningTime.Horizon(date);
        var schedule = new Schedule(target.AssetId, ScheduleType.DayAhead, target.MarketBidArea, 1,
            Enumerable.Range(0, 24).Select(hour => new ScheduleWindow(horizon.Start.AddHours(hour),
                horizon.Start.AddHours(hour + 1), hour == 2 ? -10 : 0)).ToArray());
        var compiler = Assert.Single(app.Services.GetServices<IEquipmentScheduleCompiler>());
        var actions = compiler.Compile(target, date, schedule);
        Assert.Equal(4, actions.Count);
        var snapshot = JsonSerializer.Deserialize<ShadowPlanSnapshot>(actions[0].PayloadJson)!;
        Assert.Contains(snapshot.Windows.SelectMany(window => window.Intervals), interval => interval.EnableGridCharge && interval.SocPercent == 99);
        Assert.All(snapshot.Windows.SelectMany(window => window.Intervals), interval => Assert.InRange(interval.SocPercent, 30, 99));
    }

    [Fact]
    public async Task Deye_reserve_must_be_part_of_the_EMS_optimization_model()
    {
        await using var app = BessHostBuilder.BuildApp(Arguments());
        var options = app.Services.GetRequiredService<EmsPlanningOptions>();
        var target = Assert.Single(options.Targets) with { ReserveSocPercent = null };
        var compiler = Assert.Single(app.Services.GetServices<IEquipmentScheduleCompiler>());
        var date = new DateOnly(2026, 10, 8);
        var horizon = EmsPlanningTime.Horizon(date);
        var schedule = new Schedule(target.AssetId, ScheduleType.DayAhead, target.MarketBidArea, 1,
            [new(horizon.Start, horizon.End, 0)]);
        var error = Assert.Throws<InvalidOperationException>(() => compiler.Compile(target, date, schedule));
        Assert.Equal("deye-operational-reserve-not-in-ems-model", error.Message);
    }

    private static string[] Arguments() =>
    [
        $"--Bess:SchemaDirectory={Path.Combine(Root(), "config", "schema")}",
        $"--Bess:AssetConfigPath={Path.Combine(Root(), "config", "examples", "asset.single-bess.json")}",
        "--Bess:TelemetrySource=", "--Bess:PriceSeriesSource=entso-e",
        "--Bess:EntsoeApiToken=test-only", "--Bess:EntsoeDomainCode=10Y1001C--00003F",
        "--Bess:ScheduleSolver:Backend=or_tools", "--Bess:Planning:Enabled=true",
        $"--Bess:Planning:PlanDirectory={Path.Combine(Path.GetTempPath(), "unused-ems-planning-composition")}",
        "--Bess:Planning:Targets:0:SiteId=single-bess-1",
        "--Bess:Planning:Targets:0:AssetId=single-bess-1",
        "--Bess:Planning:Targets:0:IntegrationId=deye_cloud",
        "--Bess:Planning:Targets:0:MarketBidArea=10Y1001C--00003F",
        "--Bess:Planning:Targets:0:PriceUnit=UAH/MWh",
        "--Bess:Planning:Targets:0:ReserveSocPercent=30",
    ];

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BatteryEms.sln"))) { return directory.FullName; }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
