using System.Net;
using System.Text.Json;
using BatteryEms.Adapters.Optimization.Deye;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Assets;
using BatteryEms.Application.Planning;
using BatteryEms.Application.Time;
using BatteryEms.Domain;
using BatteryEms.Host.Planning;
using Xunit;

namespace BatteryEms.ArchitectureTests;

public sealed class EmsBrokerDispatchTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Dispatcher_sends_only_authority_reference_and_requires_verified_broker_readback()
    {
        var envelope = Envelope();
        using var handler = new RecordingHandler(HttpStatusCode.OK, new DeviceWriteBrokerExecutionResult(
            true, true, true, DeviceWriteBrokerAttemptState.Verified, "verified"));
        using var client = new HttpClient(handler);
        var dispatcher = new HttpBrokerPlanDispatcher(client, Options(), new TestClock(envelope.Claim.ClaimedAtUtc));
        var result = await dispatcher.DispatchAsync(envelope, CancellationToken.None);
        Assert.Equal(ActivationDispatchOutcome.Succeeded, result.Outcome);
        Assert.Equal(new Uri("https://broker.example/v1/device-writes"), handler.Address);
        Assert.Equal("Bearer test-only-token", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(envelope.Claim.ClaimId, body.RootElement.GetProperty("attemptId").GetGuid());
        Assert.Equal(envelope.Claim.ClaimId, body.RootElement.GetProperty("activationClaimId").GetGuid());
        Assert.Equal(envelope.Claim.PayloadHash, body.RootElement.GetProperty("payloadHash").GetString());
        Assert.False(body.RootElement.TryGetProperty("payloadJson", out _));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, DeviceWriteBrokerAttemptState.Unknown)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DeviceWriteBrokerAttemptState.NotSent)]
    public async Task Unverified_or_http_failure_is_unknown_and_never_automatically_retried(
        HttpStatusCode status, DeviceWriteBrokerAttemptState state)
    {
        var envelope = Envelope();
        using var handler = new RecordingHandler(status, new DeviceWriteBrokerExecutionResult(true, false, false, state, "unknown"));
        using var client = new HttpClient(handler);
        var dispatcher = new HttpBrokerPlanDispatcher(client, Options(), new TestClock(envelope.Claim.ClaimedAtUtc));
        var result = await dispatcher.DispatchAsync(envelope, CancellationToken.None);
        Assert.Equal(ActivationDispatchOutcome.Unknown, result.Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Expired_claim_prevents_broker_transport()
    {
        var envelope = Envelope();
        using var handler = new RecordingHandler(HttpStatusCode.OK, new DeviceWriteBrokerExecutionResult(
            true, true, true, DeviceWriteBrokerAttemptState.Verified, "verified"));
        using var client = new HttpClient(handler);
        var dispatcher = new HttpBrokerPlanDispatcher(client, Options(), new TestClock(envelope.Claim.ExpiresAtUtc));
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(envelope, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    private static EmsPlanningOptions Options() => new()
    { BrokerBaseUrl = new Uri("https://broker.example/"), BrokerToken = "test-only-token" };

    private static ActivationDispatchEnvelope Envelope()
    {
        var date = new DateOnly(2026, 10, 8);
        var horizon = EmsPlanningTime.Horizon(date);
        var schedule = new Schedule("asset", ScheduleType.DayAhead, "UA", 1,
            Enumerable.Range(0, 24).Select(hour => new ScheduleWindow(horizon.Start.AddHours(hour), horizon.Start.AddHours(hour + 1), 0)).ToArray());
        var compiler = new DeyeEquipmentScheduleCompiler(new(), new InMemoryBatteryAssetRegistry([
            new BatteryAsset("asset", 400, 160, 160, 30, 100, 0.95, 0.95, 10, -10, 50)]));
        var action = compiler.Compile(new("site", "asset", "deye_cloud", "UA", "UAH/MWh"), date, schedule)[0];
        var plan = JsonSerializer.Deserialize<ShadowPlanSnapshot>(action.PayloadJson)!;
        var now = action.ScheduledAtUtc.AddSeconds(1);
        var claim = new ActivationPrewriteClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "site", "Z1", ActivationPayloadIntegrity.ComputeWindowHash(plan.Windows[0]), "executor", "writer",
            1, 1, ActivationPayloadIntegrity.ComputeHash(plan), action.PayloadJson, now, now.AddSeconds(15));
        return new(claim, plan, plan.Windows[0]);
    }

    private sealed class TestClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class RecordingHandler(HttpStatusCode status, DeviceWriteBrokerExecutionResult result) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Address { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Address = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(result, JsonOptions)) };
        }
    }
}
