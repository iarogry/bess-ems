using BatteryEms.Application.IO;
using BatteryEms.Application.Orchestration;
using BatteryEms.Api.Scheduling;
using BatteryEms.Api.Endpoints;
using BatteryEms.Api.Composition;
using BatteryEms.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BatteryEms.ArchitectureTests;

public sealed class ShadowHostCompositionTests
{
    [Fact]
    public async Task Shadow_mode_is_disabled_by_default_and_no_legacy_source_is_registered()
    {
        await using var app = BessHostBuilder.BuildApp(BaseArguments());

        Assert.Null(app.Services.GetService<ILegacyPlanSnapshotSource>());
        Assert.Null(app.Services.GetService<ShadowDayAheadScheduler>());
        Assert.NotNull(app.Services.GetRequiredService<IShadowRunTrigger>());
        Assert.Contains(WriterSafetyRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.Contains(PilotReadinessRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(ShadowRunRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(ActivationProposalRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(ActivationApprovalRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(ActivationReleaseRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(ActivationRollbackRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(PilotArmRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.IsType<NoOpBatteryCommandSink>(app.Services.GetRequiredService<IBatteryCommandSink>());
        Assert.IsType<FailClosedActivationPrewriteClaimStore>(
            app.Services.GetRequiredService<IActivationPrewriteClaimStore>());
        Assert.IsType<FailClosedActivationPlanDispatcher>(
            app.Services.GetRequiredService<IActivationPlanDispatcher>());
    }

    [Fact]
    public void Shadow_mode_fails_closed_without_legacy_scenario_directory()
    {
        var arguments = BaseArguments().Append("--Bess:ShadowModeEnabled=true").ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            BessHostBuilder.BuildApp(arguments));

        Assert.Contains("LegacyScenarioDirectory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_cannot_be_enabled_without_shadow_mode()
    {
        var arguments = BaseArguments()
            .Append("--Bess:ShadowSchedulerEnabled=true")
            .ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            BessHostBuilder.BuildApp(arguments));

        Assert.Contains("ShadowModeEnabled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_cutover_cannot_be_enabled_without_shadow_mode()
    {
        var arguments = BaseArguments()
            .Append("--Bess:ActivationCutoverEnabled=true")
            .ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            BessHostBuilder.BuildApp(arguments));

        Assert.Contains("ShadowModeEnabled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_cutover_requires_durable_persistence()
    {
        var arguments = ShadowArguments()
            .Append("--Bess:ActivationCutoverEnabled=true")
            .Append("--Bess:ActivationPilotEnabled=true")
            .ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            BessHostBuilder.BuildApp(arguments));

        Assert.Contains("PersistenceConnectionString", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_pilot_cannot_be_enabled_without_cutover()
    {
        var arguments = ShadowArguments()
            .Append("--Bess:ActivationPilotEnabled=true")
            .ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            BessHostBuilder.BuildApp(arguments));

        Assert.Contains("ActivationCutoverEnabled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pilot_and_cutover_routes_require_both_explicit_opt_ins()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddBessApplicationInMemoryStores();
        await using var app = builder.Build();
        app.MapAgentEndpoints(
            shadowControlEnabled: true,
            activationCutoverEnabled: true,
            activationPilotEnabled: true);

        var routes = RoutePatterns(app).ToArray();
        Assert.Contains(ActivationReleaseRoute, routes, StringComparer.Ordinal);
        Assert.Contains(ActivationRollbackRoute, routes, StringComparer.Ordinal);
        Assert.Contains(PilotArmRoute, routes, StringComparer.Ordinal);
        Assert.Contains(PilotAbortRoute, routes, StringComparer.Ordinal);
    }

    [Fact]
    public void Pilot_readiness_window_cannot_be_weakened_below_seven_days()
    {
        var arguments = BaseArguments()
            .Append("--Bess:PilotReadinessRequiredDays=6")
            .ToArray();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            BessHostBuilder.BuildApp(arguments));

        Assert.Contains("between 7", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_fails_closed_without_explicit_site_and_local_time()
    {
        var arguments = ShadowArguments()
            .Append("--Bess:ShadowSchedulerEnabled=true")
            .ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            BessHostBuilder.BuildApp(arguments));

        Assert.Contains("ShadowSiteId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_fails_closed_without_durable_persistence_or_explicit_local_override()
    {
        var arguments = ShadowArguments()
            .Append("--Bess:ShadowSchedulerEnabled=true")
            .Append("--Bess:ShadowSiteId=site-shadow")
            .Append("--Bess:ShadowTriggerHourLocal=14")
            .Append("--Bess:ShadowTriggerMinuteLocal=0")
            .ToArray();

        var error = Assert.Throws<InvalidOperationException>(() =>
            BessHostBuilder.BuildApp(arguments));

        Assert.Contains("PersistenceConnectionString", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shadow_mode_registers_import_before_comparison_and_keeps_noop_writer()
    {
        var arguments = BaseArguments()
            .Append("--Bess:ShadowModeEnabled=true")
            .Append($"--Bess:LegacyScenarioDirectory={RepoPath("config", "scenarios")}")
            .ToArray();
        await using var app = BessHostBuilder.BuildApp(arguments);

        Assert.NotNull(app.Services.GetRequiredService<ILegacyPlanSnapshotSource>());
        var modules = app.Services.GetServices<IOrchestrationModule>()
            .OrderBy(module => module.StepOrder)
            .ToArray();
        Assert.Collection(
            modules,
            module => Assert.IsType<LegacyPlanSnapshotImportModule>(module),
            module => Assert.IsType<ScheduleShadowPlanProjectionModule>(module),
            module => Assert.IsType<ShadowPlanComparisonModule>(module));
        Assert.NotNull(app.Services.GetRequiredService<IShadowRunTrigger>());
        Assert.Null(app.Services.GetService<ShadowDayAheadScheduler>());
        Assert.Contains(WriterSafetyRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.Contains(PilotReadinessRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.Contains(ShadowRunRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.Contains(ActivationProposalRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.Contains(ActivationApprovalRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(ActivationReleaseRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(ActivationRollbackRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.DoesNotContain(PilotArmRoute, RoutePatterns(app), StringComparer.Ordinal);
        Assert.IsType<NoOpBatteryCommandSink>(app.Services.GetRequiredService<IBatteryCommandSink>());
        Assert.IsType<FailClosedActivationPrewriteClaimStore>(
            app.Services.GetRequiredService<IActivationPrewriteClaimStore>());
        Assert.IsType<FailClosedActivationPlanDispatcher>(
            app.Services.GetRequiredService<IActivationPlanDispatcher>());
    }

    [Fact]
    public async Task Scheduler_is_separately_opted_in_and_keeps_noop_writer()
    {
        var arguments = ShadowArguments()
            .Append("--Bess:ShadowSchedulerEnabled=true")
            .Append("--Bess:ShadowSiteId=site-shadow")
            .Append("--Bess:ShadowTriggerHourLocal=14")
            .Append("--Bess:ShadowTriggerMinuteLocal=0")
            .Append("--Bess:ShadowSchedulerAllowInMemory=true")
            .ToArray();
        await using var app = BessHostBuilder.BuildApp(arguments);

        Assert.NotNull(app.Services.GetRequiredService<ShadowDayAheadScheduler>());
        Assert.Single(app.Services.GetServices<IHostedService>()
            .OfType<ShadowDayAheadSchedulerHostedService>());
        Assert.IsType<NoOpBatteryCommandSink>(app.Services.GetRequiredService<IBatteryCommandSink>());
    }

    private const string ShadowRunRoute = "/agent/sites/{siteId}/shadow-runs/{deliveryDate}";
    private const string ActivationProposalRoute =
        "/agent/sites/{siteId}/activation-proposals/{deliveryDate}";
    private const string ActivationApprovalRoute =
        "/agent/activation-proposals/{proposalId:guid}/approve";
    private const string WriterSafetyRoute = "/agent/sites/{siteId}/writer-safety";
    private const string PilotReadinessRoute = "/agent/sites/{siteId}/pilot-readiness";
    private const string ActivationReleaseRoute =
        "/agent/activation-proposals/{proposalId:guid}/release";
    private const string ActivationRollbackRoute =
        "/agent/sites/{siteId}/writer-safety/rollback";
    private const string PilotArmRoute =
        "/agent/activation-proposals/{proposalId:guid}/pilot-sessions";
    private const string PilotAbortRoute =
        "/agent/activation-pilot-sessions/{sessionId:guid}/abort";

    private static IEnumerable<string> RoutePatterns(Microsoft.AspNetCore.Builder.WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty);

    private static string[] BaseArguments() =>
    [
        $"--Bess:SchemaDirectory={RepoPath("config", "schema")}",
        $"--Bess:AssetConfigPath={RepoPath("config", "examples", "asset.single-bess.json")}",
        "--Bess:TelemetrySource=",
        "--Bess:PriceSeriesSource=",
    ];

    private static IEnumerable<string> ShadowArguments() =>
        BaseArguments()
            .Append("--Bess:ShadowModeEnabled=true")
            .Append($"--Bess:LegacyScenarioDirectory={RepoPath("config", "scenarios")}");

    private static string RepoPath(params string[] parts) =>
        Path.Combine([RepoRoot(), .. parts]);

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        for (var index = 0; index < 10 && directory is not null; index++)
        {
            if (File.Exists(Path.Combine(directory, "BatteryEms.sln")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root containing BatteryEms.sln.");
    }
}
