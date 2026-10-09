using System.Net;
using System.Text.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;
using BatteryEms.Host.Planning;
using Xunit;

namespace BatteryEms.ArchitectureTests;

public sealed class EmsDailyExecutionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExpectedWindows = ["Z1", "Z2", "Z3", "Z4"];

    [Fact]
    public async Task Prepared_optimized_day_flows_through_four_automatic_HTTP_broker_actions_once_each()
    {
        using var fixture = new EmsPlanningCycleTests.Fixture();
        await fixture.Scheduler(activate: false).RunOnceAsync(CancellationToken.None);
        var plan = await fixture.Store.FindAsync(fixture.Target.AssetId, EmsPlanningCycleTests.Fixture.Date, CancellationToken.None);
        Assert.NotNull(plan);
        Assert.Equal(1, fixture.Prices.Calls);
        var authorizations = CreateAuthorizations(plan, fixture.Clock.UtcNow);
        var safety = await CreateSafetyAsync(fixture);
        var options = new EmsPlanningOptions { ActivationEnabled = true, DayAuthorizationsEnabled = true,
            WriterOwnerId = "ems-owner", BrokerBaseUrl = new Uri("https://broker.example/"), BrokerToken = "test-token" };
        using var handler = new BrokerHandler();
        using var client = new HttpClient(handler);
        var dispatcher = new HttpBrokerPlanDispatcher(client, options, fixture.Clock);
        var executor = new DeyeDayPlanExecutor(options, authorizations, safety, dispatcher, fixture.Clock);
        var scheduler = fixture.Scheduler(executor: executor);
        foreach (var action in plan.Actions)
        {
            fixture.Clock.UtcNow = action.ScheduledAtUtc.AddSeconds(1);
            await scheduler.RunOnceAsync(CancellationToken.None);
            // Reconstructing the executor simulates a process restart. Claims
            // remain in the authoritative store, independent of local state.
            var restarted = new DeyeDayPlanExecutor(options, authorizations, safety, dispatcher, fixture.Clock);
            await fixture.Scheduler(executor: restarted).RunOnceAsync(CancellationToken.None);
        }
        Assert.Equal(4, handler.Windows.Count);
        Assert.Equal(ExpectedWindows, handler.Windows);
        Assert.Equal(4, authorizations.Claims.Count);
    }

    private static TestDayAuthorizations CreateAuthorizations(EquipmentDayPlan plan, DateTimeOffset now)
    {
        var payload = JsonSerializer.Deserialize<ShadowPlanSnapshot>(plan.Actions[0].PayloadJson)!;
        return new(new(Guid.NewGuid(), Guid.NewGuid(), plan.Target.SiteId,
            plan.DeliveryDate, ActivationPayloadIntegrity.ComputeHash(payload), "ems-owner", 2,
            now, plan.Actions[^1].DeadlineUtc, false), payload);
    }

    private static async Task<InMemoryActivationWriterSafetyStore> CreateSafetyAsync(EmsPlanningCycleTests.Fixture fixture)
    {
        var safety = new InMemoryActivationWriterSafetyStore();
        var initial = ActivationSafetyState.InitialFailClosed(fixture.Target.SiteId, fixture.Clock.UtcNow, "operator", "synthetic setup");
        Assert.True(await safety.CompareExchangeStateAsync(initial, null, CancellationToken.None));
        Assert.True(await safety.CompareExchangeStateAsync(initial with { Revision = 2, KillSwitchEngaged = false,
            WriterAuthority = ActivationWriterAuthority.ProductAgent, LegacyWriterStoppedAtUtc = fixture.Clock.UtcNow,
            LegacyStopEvidence = "synthetic test only" }, 1, CancellationToken.None));
        return safety;
    }

    private sealed class TestDayAuthorizations(DeyeDayAuthorization authorization, ShadowPlanSnapshot payload) : IDeyeDayAuthorizationStore
    {
        public Dictionary<string, DeyeDayWindowClaim> Claims { get; } = new(StringComparer.Ordinal);
        public Task<DeyeDayAuthorization?> FindAsync(string siteId, DateOnly deliveryDate, CancellationToken cancellationToken) =>
            Task.FromResult<DeyeDayAuthorization?>(siteId == authorization.SiteId && deliveryDate == authorization.DeliveryDate ? authorization : null);
        public Task<DeyeDayWindowClaimResult> ClaimAsync(DeyeDayWindowClaimRequest request, CancellationToken cancellationToken)
        {
            if (Claims.TryGetValue(request.WindowId, out var previous)) { return Task.FromResult(new DeyeDayWindowClaimResult(previous, true, true)); }
            var window = payload.Windows.Single(item => item.WindowId == request.WindowId);
            var claim = new DeyeDayWindowClaim(Guid.NewGuid(), authorization.AuthorizationId, request.SiteId,
                request.DeliveryDate, request.WindowId, request.PayloadHash, ActivationPayloadIntegrity.ComputeWindowHash(window),
                request.WriterOwnerId, request.SafetyRevision, request.FencingToken, request.NowUtc, request.NowUtc.AddSeconds(30));
            Claims.Add(request.WindowId, claim);
            return Task.FromResult(new DeyeDayWindowClaimResult(claim, true));
        }
        public Task<DeyeDayAuthorizationResult> AuthorizeAsync(DeyeDayAuthorizationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(Guid authorizationId, string actor, string reason, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class BrokerHandler : HttpMessageHandler
    {
        public System.Collections.ObjectModel.Collection<string> Windows { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal(body.RootElement.GetProperty("attemptId").GetGuid(), body.RootElement.GetProperty("activationClaimId").GetGuid());
            Windows.Add(body.RootElement.GetProperty("windowId").GetString()!);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                new DeviceWriteBrokerExecutionResult(true, true, true, DeviceWriteBrokerAttemptState.Verified, "verified"), JsonOptions)) };
        }
    }
}
