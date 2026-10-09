using BatteryEms.Adapters.Optimization.Deye;
using BatteryEms.Adapters.Optimization.OrTools;
using BatteryEms.Application.Assets;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Persistence;
using BatteryEms.Application.Time;
using BatteryEms.Domain;
using BatteryEms.Host.Planning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BatteryEms.ArchitectureTests;

public sealed class EmsPlanningCycleTests
{
    [Fact]
    public async Task Startup_preparation_retries_prices_without_activating_or_publishing_schedule()
    {
        using var fixture = new Fixture();
        fixture.Clock.UtcNow = EmsPlanningTime.ToUtc(Fixture.Date.AddDays(-1), new TimeOnly(12, 0));
        fixture.RefreshTelemetry();
        fixture.Prices.Fail = true;
        var scheduler = fixture.Scheduler(activate: false, prepareOnStartup: true);
        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.Null(await fixture.Store.FindAsync("asset-a", Fixture.Date, CancellationToken.None));
        fixture.Prices.Fail = false;
        await scheduler.RunOnceAsync(CancellationToken.None);
        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(await fixture.Store.FindAsync("asset-a", Fixture.Date, CancellationToken.None));
        Assert.Equal(2, fixture.Prices.Calls);
        Assert.Null(fixture.Schedules.FindActive("asset-a", ScheduleType.DayAhead));
        Assert.Empty(fixture.Executor.Actions);
    }

    [Fact]
    public async Task Daily_trigger_uses_Kyiv_time_and_restart_reuses_saved_four_window_plan()
    {
        using var fixture = new Fixture();
        var scheduler = fixture.Scheduler();
        fixture.Clock.UtcNow = EmsPlanningTime.ToUtc(Fixture.Date.AddDays(-1), new TimeOnly(14, 59));
        fixture.RefreshTelemetry();
        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Prices.Calls);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        fixture.RefreshTelemetry();
        await scheduler.RunOnceAsync(CancellationToken.None);
        var plan = await fixture.Store.FindAsync("asset-a", Fixture.Date, CancellationToken.None);
        Assert.NotNull(plan);
        Assert.Equal(4, plan.Actions.Count);
        Assert.Equal(30, plan.InitialSocPercent);
        Assert.Null(fixture.Schedules.FindActive("asset-a", ScheduleType.DayAhead));
        var restarted = fixture.Scheduler(new FileEquipmentDayPlanStore(fixture.Directory));
        fixture.Clock.UtcNow = EmsPlanningTime.ToUtc(Fixture.Date.AddDays(-1), new TimeOnly(23, 55));
        await restarted.RunOnceAsync(CancellationToken.None);
        await restarted.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Prices.Calls);
        Assert.All(fixture.Executor.Actions, action => Assert.Equal("Z1", action));
        Assert.Equal(2, fixture.Executor.Actions.Count);
        // Integration executors own durable device-command dedupe; repeated
        // scheduling deliberately does not assert a second physical write.
    }

    [Fact]
    public async Task Missing_prices_retry_without_overwriting_today_and_stale_vendor_timestamp_blocks()
    {
        using var fixture = new Fixture();
        fixture.Prices.Fail = true;
        await fixture.Scheduler().RunOnceAsync(CancellationToken.None);
        Assert.Null(await fixture.Store.FindAsync("asset-a", Fixture.Date, CancellationToken.None));
        fixture.Prices.Fail = false;
        await fixture.Scheduler().RunOnceAsync(CancellationToken.None);
        Assert.NotNull(await fixture.Store.FindAsync("asset-a", Fixture.Date, CancellationToken.None));
        var later = Fixture.Date.AddDays(1);
        fixture.RefreshTelemetry(fixture.Clock.UtcNow.AddHours(-1));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Planner().PlanAsync(
            fixture.Target, later, fixture.Clock.UtcNow, CancellationToken.None));
        Assert.Equal("planning-telemetry-not-usable", error.Message);
        Assert.Null(await fixture.Store.FindAsync("asset-a", later, CancellationToken.None));
    }

    [Theory]
    [InlineData(2026, 3, 29, 23)]
    [InlineData(2026, 10, 25, 25)]
    public async Task Dst_days_are_supported_by_EMS_and_streaming_but_explicitly_blocked_by_Deye(
        int year, int month, int day, int hours)
    {
        using var fixture = new Fixture();
        var date = new DateOnly(year, month, day);
        var target = fixture.Target with { IntegrationId = "mqtt" };
        var streaming = new TrackedScheduleIntegration("mqtt", fixture.Schedules);
        var planner = new EmsDayAheadPlanner([fixture.Model()], fixture.Store, [streaming]);
        var plan = await planner.PlanAsync(target, date, fixture.Clock.UtcNow, CancellationToken.None);
        Assert.Equal(hours, plan.Schedule.Windows.Count);
        Assert.Single(plan.Actions);
        var deye = fixture.Compiler();
        var error = Assert.Throws<InvalidOperationException>(() => deye.Compile(fixture.Target, date, plan.Schedule));
        Assert.Contains("dst-day-unsupported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_schedule_is_published_at_midnight_and_publication_is_idempotent()
    {
        using var fixture = new Fixture();
        var integration = new TrackedScheduleIntegration("modbus", fixture.Schedules);
        var planner = new EmsDayAheadPlanner([fixture.Model()], fixture.Store, [integration]);
        var plan = await planner.PlanAsync(fixture.Target with { IntegrationId = "modbus" },
            Fixture.Date, fixture.Clock.UtcNow, CancellationToken.None);
        Assert.Equal(plan.Schedule.HorizonStart, Assert.Single(plan.Actions).ScheduledAtUtc);
        await integration.ExecuteAsync(plan, plan.Actions[0], CancellationToken.None);
        await integration.ExecuteAsync(plan, plan.Actions[0], CancellationToken.None);
        Assert.Equal(1, fixture.Schedules.FindActive("asset-a", ScheduleType.DayAhead)!.Version);
        Assert.Equal(24, fixture.Schedules.FindActive("asset-a", ScheduleType.DayAhead)!.Windows.Count);
    }

    [Fact]
    public async Task Concurrent_publication_keeps_a_complete_single_winning_plan()
    {
        using var fixture = new Fixture();
        var plan = await fixture.Planner().PlanAsync(fixture.Target, Fixture.Date,
            fixture.Clock.UtcNow, CancellationToken.None);
        var next = plan with { DeliveryDate = Fixture.Date.AddDays(1) };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new FileEquipmentDayPlanStore(fixture.Directory).TrySaveAsync(next, CancellationToken.None)));
        Assert.Single(results, result => result);
        var saved = await fixture.Store.FindAsync("asset-a", next.DeliveryDate, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(plan.OptimizationRunId, saved.OptimizationRunId);
        Assert.Equal(4, saved.Actions.Count);
    }

    [Fact]
    public async Task Planning_without_activation_never_invokes_equipment_executor()
    {
        using var fixture = new Fixture();
        var scheduler = fixture.Scheduler(activate: false);
        await scheduler.RunOnceAsync(CancellationToken.None);
        fixture.Clock.UtcNow = EmsPlanningTime.ToUtc(Fixture.Date.AddDays(-1), new TimeOnly(23, 55));
        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(await fixture.Store.FindAsync("asset-a", Fixture.Date, CancellationToken.None));
        Assert.Empty(fixture.Executor.Actions);
    }

    [Fact]
    public async Task Per_equipment_cost_and_measured_soc_override_host_solver_defaults()
    {
        using var fixture = new Fixture();
        fixture.RefreshTelemetry(socPercent: 80);
        var model = fixture.Model();
        var inexpensive = await model.OptimizeAsync(fixture.Target with { ThroughputCostPerKwh = 0 },
            Fixture.Date, fixture.Clock.UtcNow, CancellationToken.None);
        var expensive = await model.OptimizeAsync(fixture.Target with { ThroughputCostPerKwh = 1 },
            Fixture.Date, fixture.Clock.UtcNow, CancellationToken.None);
        Assert.Equal(80, inexpensive.InitialSocPercent);
        Assert.Equal("hold-latest-soc-assumption", inexpensive.InitialStateBasis);
        Assert.Equal(190, inexpensive.Schedule.Windows.Sum(window => window.TargetPowerKw * window.Duration.TotalHours), 5);
        Assert.All(inexpensive.Schedule.Windows, window => Assert.InRange(window.TargetPowerKw, -160, 160));
        Assert.All(expensive.Schedule.Windows, window => Assert.Equal(0, window.TargetPowerKw, 5));
    }

    [Fact]
    public void Midnight_soc_projection_uses_charge_efficiency_and_physical_capacity()
    {
        var asset = new BatteryAsset("battery", 100, 10, 10, 30, 100, 0.9, 0.9, 10, -10, 50);
        var start = EmsPlanningTime.ToUtc(new DateOnly(2026, 10, 7), new TimeOnly(15, 0));
        var end = start.AddHours(9);
        var schedule = new Schedule("battery", ScheduleType.DayAhead, "UA", 1,
            [new(start, end, -10)]);
        var projected = BatteryInitialStateProjection.Calculate(asset, 30, start, end, schedule);
        Assert.Equal(100, projected.SocPercent);
        Assert.Equal("active-schedule-with-idle-gaps-forecast", projected.Basis);
    }

    internal sealed class Fixture : IDisposable
    {
        internal static readonly DateOnly Date = new(2026, 10, 8);
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "ems-plan-test-" + Guid.NewGuid().ToString("N"));
        public TestClock Clock { get; } = new() { UtcNow = EmsPlanningTime.ToUtc(Date.AddDays(-1), new TimeOnly(15, 0)) };
        public TestPrices Prices { get; } = new();
        public InMemorySnapshotStore Telemetry { get; } = new(TimeSpan.FromMinutes(5));
        public InMemoryScheduleRepository Schedules { get; } = new();
        public TestExecutor Executor { get; } = new();
        public EquipmentPlanningTarget Target { get; } = new("site-a", "asset-a", "deye_cloud", "UA", "UAH/MWh", 1);
        public FileEquipmentDayPlanStore Store => new(Directory);
        private readonly InMemoryBatteryAssetRegistry _assets = new([new BatteryAsset(
            "asset-a", 400, 160, 160, 30, 100, 0.95, 0.95, 160, -20, 60)]);

        public Fixture() => RefreshTelemetry();
        public void RefreshTelemetry(DateTimeOffset? vendorTimestamp = null, double socPercent = 30) => Telemetry.Update(new BatteryTelemetry(
            vendorTimestamp ?? Clock.UtcNow, "asset-a", socPercent, 100, 0, 0, 400, 0, 25, true, "none", DataQuality.Valid), Clock.UtcNow);
        public BatteryDayAheadPlanningModel Model() => new(_assets, Telemetry, Prices,
            new OrToolsScheduleOptimizer(new ScheduleSolverOptions { InitialSocPercent = 100 }, Clock,
                NullLogger<OrToolsScheduleOptimizer>.Instance), new InMemoryReserveRepository(), Schedules, new InMemoryOptimizationRunRepository());
        public DeyeEquipmentScheduleCompiler Compiler() => new(new(), _assets);
        public EmsDayAheadPlanner Planner(IEquipmentDayPlanStore? store = null) => new([Model()],
            store ?? Store, [Compiler()]);
        public EmsPlanningHostedService Scheduler(IEquipmentDayPlanStore? store = null, bool activate = true, IEquipmentPlanExecutor? executor = null, bool prepareOnStartup = false)
        {
            var options = new EmsPlanningOptions { Enabled = true, ActivationEnabled = activate, PrepareOnStartup = prepareOnStartup, PlanDirectory = Directory };
            options.Targets.Add(Target);
            return new(options, Planner(store), store ?? Store, [executor ?? Executor], new UnusedShadowTrigger(), [], Clock,
                NullLogger<EmsPlanningHostedService>.Instance);
        }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, true); } }
    }

    internal sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }
    internal sealed class TestPrices : IPriceSeriesSource
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<PriceSeries> LoadAsync(PriceSeriesRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) { throw new InvalidOperationException("prices-not-published"); }
            var count = (int)(request.HorizonEnd - request.HorizonStart).TotalHours;
            return Task.FromResult(new PriceSeries(request.MarketBidArea, request.Product, request.PriceKind,
                "UAH/MWh", "entso-e", request.HorizonStart, request.HorizonEnd, request.TimeStep,
                Enumerable.Repeat(100d, count).ToArray()));
        }
    }
    internal sealed class TestExecutor : IEquipmentPlanExecutor
    {
        public string IntegrationId => "deye_cloud";
        public System.Collections.ObjectModel.Collection<string> Actions { get; } = [];
        public Task ExecuteAsync(EquipmentDayPlan plan, EquipmentPlanAction action, CancellationToken cancellationToken)
        { Actions.Add(action.ActionId); return Task.CompletedTask; }
    }
    private sealed class UnusedShadowTrigger : IShadowRunTrigger
    {
        public Task<OrchestrationRun> StartAsync(string siteId, DateOnly deliveryDate,
            OrchestrationTriggerType triggerType, CancellationToken cancellationToken) => throw new InvalidOperationException("unexpected-shadow-call");
    }
}
