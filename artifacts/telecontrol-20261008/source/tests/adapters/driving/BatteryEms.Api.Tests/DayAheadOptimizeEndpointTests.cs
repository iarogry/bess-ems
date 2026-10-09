using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatteryEms.Application.Assets;
using BatteryEms.Application.Optimization;
using BatteryEms.Application.Persistence;
using BatteryEms.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class DayAheadOptimizeEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private static readonly DateTimeOffset HorizonStart =
        new(2026, 5, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly BatteryEmsApiFactory _factory;

    public DayAheadOptimizeEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Returns_401_without_token()
    {
        using var client = _factory.CreateClient();
        var body = ValidBody();
        var response = await client.PostAsJsonAsync("/markets/day-ahead/optimize", body, TestJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns_403_with_viewer_token()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BatteryEmsApiFactory.ViewerToken);
        var body = ValidBody();
        var response = await client.PostAsJsonAsync("/markets/day-ahead/optimize", body, TestJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Returns_400_when_required_field_is_missing()
    {
        using var client = AuthenticatedClient();
        var body = new
        {
            asset_id = "",
            schedule_type = "day_ahead",
            horizon_start = HorizonStart,
            horizon_end = HorizonStart + TimeSpan.FromHours(1),
            time_step_seconds = 3600,
        };
        var response = await client.PostAsJsonAsync("/markets/day-ahead/optimize", body, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_404_when_asset_is_not_registered()
    {
        using var client = AuthenticatedClient();
        var body = ValidBody(assetId: "no-such-asset");
        var response = await client.PostAsJsonAsync("/markets/day-ahead/optimize", body, TestJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Returns_400_for_unknown_schedule_type()
    {
        using var client = AuthenticatedClient();
        SeedAsset();
        var body = new
        {
            asset_id = "asset-opt-1",
            schedule_type = "fortnight_ahead",
            horizon_start = HorizonStart,
            horizon_end = HorizonStart + TimeSpan.FromHours(1),
            time_step_seconds = 3600,
        };
        var response = await client.PostAsJsonAsync("/markets/day-ahead/optimize", body, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_200_and_persisted_failed_run_with_noop_solver()
    {
        using var client = AuthenticatedClient();
        SeedAsset();
        var body = ValidBody(assetId: "asset-opt-1");

        var response = await client.PostAsJsonAsync("/markets/day-ahead/optimize", body, TestJson.Options);

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<OptimizationDto>(TestJson.Options);
        Assert.NotNull(dto);
        Assert.NotEqual(Guid.Empty, dto!.RunId);
        Assert.Equal("failed", dto.Status);  // snake_case enum converter, NoOp stub
        Assert.Equal(HorizonStart, dto.HorizonStart);
        Assert.Equal(HorizonStart + TimeSpan.FromHours(1), dto.HorizonEnd);
        Assert.Null(dto.ProducedScheduleVersion);
        Assert.Equal("no-solver-configured", dto.TerminationReason);

        var runs = _factory.Services.GetRequiredService<IOptimizationRunRepository>();
        var stored = await runs.FindByIdAsync(dto.RunId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(OptimizationSolverStatus.Failed, stored!.Status);
    }

    [Fact]
    public async Task Get_optimization_run_returns_persisted_status_payload()
    {
        using var client = AuthenticatedClient();
        SeedAsset();
        var body = ValidBody(assetId: "asset-opt-1");
        var post = await client.PostAsJsonAsync("/markets/day-ahead/optimize", body, TestJson.Options);
        post.EnsureSuccessStatusCode();
        var posted = await post.Content.ReadFromJsonAsync<OptimizationDto>(TestJson.Options);

        var response = await client.GetAsync($"/optimization/runs/{posted!.RunId}");

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<OptimizationRunDto>(TestJson.Options);
        Assert.NotNull(dto);
        Assert.Equal(posted.RunId, dto!.RunId);
        Assert.Equal("asset-opt-1", dto.AssetId);
        Assert.Equal("failed", dto.Status);
        Assert.Equal(HorizonStart, dto.HorizonStart);
        Assert.Equal(HorizonStart + TimeSpan.FromHours(1), dto.HorizonEnd);
        Assert.Equal(3600, dto.TimeStepSeconds);
        Assert.Equal("no-solver-configured", dto.TerminationReason);
        Assert.Null(dto.ProducedSchedule);
    }

    [Fact]
    public async Task Successful_optimization_response_includes_schedule_economics()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IScheduleOptimizer>();
                services.AddSingleton<IScheduleOptimizer, EconomicScheduleOptimizer>();
            });
        });
        using var client = AuthenticatedClient(factory);
        SeedAsset(factory, "asset-pnl-1");
        var body = new
        {
            asset_id = "asset-pnl-1",
            schedule_type = "day_ahead",
            horizon_start = HorizonStart,
            horizon_end = HorizonStart + TimeSpan.FromHours(2),
            time_step_seconds = 3600,
            prices_per_step = new[] { 10.0, 10000.0 },
            price_unit = "UAH/MWh",
        };

        var response = await client.PostAsJsonAsync("/markets/day-ahead/optimize", body, TestJson.Options);

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<OptimizationWithEconomicsDto>(TestJson.Options);
        Assert.NotNull(dto);
        Assert.Equal("optimal", dto!.Status);
        Assert.NotNull(dto.Economics);
        Assert.Equal("UAH", dto.Economics!.Currency);
        Assert.Equal(1.6, dto.Economics.TotalCost, precision: 6);
        Assert.Equal(1600.0, dto.Economics.TotalRevenue, precision: 6);
        Assert.True(dto.Economics.TotalLossesKwh > 0);
        Assert.Equal(1598.4, dto.Economics.NetProfit, precision: 6);
        Assert.True(dto.Economics.Steps[0].LossesKwh > 0);
        Assert.Equal(-1.6, dto.Economics.Steps[0].NetProfit, precision: 6);
        Assert.Equal(1598.4, dto.Economics.Steps[1].CumulativeNetProfit, precision: 6);
    }

    [Fact]
    public async Task Get_optimization_run_returns_404_for_unknown_run()
    {
        using var client = AuthenticatedClient();

        var response = await client.GetAsync($"/optimization/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient AuthenticatedClient()
    {
        return AuthenticatedClient(_factory);
    }

    private static HttpClient AuthenticatedClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BatteryEmsApiFactory.OperatorToken);
        return client;
    }

    private void SeedAsset()
    {
        SeedAsset(_factory, "asset-opt-1");
    }

    private static void SeedAsset(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string assetId)
    {
        var assets = (InMemoryBatteryAssetRegistry)factory.Services.GetRequiredService<IBatteryAssetRegistry>();
        if (assets.Find(assetId) is null)
        {
            assets.Register(new BatteryAsset(
                assetId: assetId,
                capacityKwh: 100,
                maxChargePowerKw: 50,
                maxDischargePowerKw: 50,
                minSocPercent: 10,
                maxSocPercent: 90,
                chargeEfficiency: 0.95,
                dischargeEfficiency: 0.95,
                maxRampKwPerSecond: 25,
                minOperatingTemperatureCelsius: -20,
                maxOperatingTemperatureCelsius: 55));
        }
    }

    private static object ValidBody(string assetId = "asset-opt-1") => new
    {
        asset_id = assetId,
        schedule_type = "day_ahead",
        horizon_start = HorizonStart,
        horizon_end = HorizonStart + TimeSpan.FromHours(1),
        time_step_seconds = 3600,
    };

    private sealed record OptimizationDto(
        Guid RunId,
        string Status,
        DateTimeOffset HorizonStart,
        DateTimeOffset HorizonEnd,
        int? ProducedScheduleVersion,
        string TerminationReason);

    private sealed record OptimizationRunDto(
        Guid RunId,
        string AssetId,
        string Status,
        DateTimeOffset HorizonStart,
        DateTimeOffset HorizonEnd,
        double TimeStepSeconds,
        string TerminationReason,
        ScheduleReferenceDto? ProducedSchedule);

    private sealed record OptimizationWithEconomicsDto(
        Guid RunId,
        string Status,
        ScheduleEconomicsDto? Economics);

    private sealed record ScheduleEconomicsDto(
        string PriceUnit,
        string Currency,
        double TotalCost,
        double TotalRevenue,
        double TotalLossesKwh,
        double NetProfit,
        IReadOnlyList<ScheduleEconomicsStepDto> Steps);

    private sealed record ScheduleEconomicsStepDto(
        double Price,
        double TargetPowerKw,
        double EnergyMwh,
        double BatteryEnergyDeltaKwh,
        double LossesKwh,
        double Cost,
        double Revenue,
        double NetProfit,
        double CumulativeNetProfit);

    private sealed record ScheduleReferenceDto(
        string AssetId,
        string Type,
        int Version);

    private sealed class EconomicScheduleOptimizer : IScheduleOptimizer
    {
        public Task<ScheduleOptimizationResult> OptimizeAsync(
            ScheduleOptimizationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var windows = new[]
            {
                new ScheduleWindow(request.HorizonStart, request.HorizonStart + request.TimeStep, -160),
                new ScheduleWindow(request.HorizonStart + request.TimeStep, request.HorizonEnd, 160),
            };
            var schedule = new Schedule(
                request.AssetId,
                request.ScheduleType,
                request.MarketBidArea,
                request.BaseScheduleVersion + 1,
                windows);
            var produced = new ScheduleReference(schedule.AssetId, schedule.Type, schedule.Version);
            var run = new OptimizationRun(
                runId: Guid.NewGuid(),
                assetId: request.AssetId,
                solverName: "economic-schedule-optimizer",
                status: OptimizationSolverStatus.Optimal,
                horizonStart: request.HorizonStart,
                horizonEnd: request.HorizonEnd,
                timeStep: request.TimeStep,
                objectiveValue: -1598.4,
                objectiveBreakdown: new OptimizationObjectiveBreakdown(new[]
                {
                    new OptimizationObjectiveComponent("energy_cost", -1598.4, "UAH"),
                }),
                constraintViolations: Array.Empty<string>(),
                warnings: Array.Empty<string>(),
                solverRuntime: TimeSpan.Zero,
                terminationCode: "solver_finished",
                terminationDetail: null,
                createdAt: request.HorizonStart,
                inputs: request.Inputs,
                producedSchedule: produced);
            return Task.FromResult(new ScheduleOptimizationResult(run, schedule));
        }
    }
}
