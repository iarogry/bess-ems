using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class AgentWriterSafetyEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private readonly BatteryEmsApiFactory _factory;

    public AgentWriterSafetyEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_request_is_rejected()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/agent/sites/site-safety-anonymous/writer-safety");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_state_is_reported_as_configured_false_and_kill_switch_engaged()
    {
        using var client = ViewerClient();

        var response = await client.GetFromJsonAsync<Response>(
            "/agent/sites/site-safety-unconfigured/writer-safety",
            TestJson.Options);

        Assert.NotNull(response);
        Assert.False(response!.Configured);
        Assert.True(response.KillSwitchEngaged);
        Assert.Equal(ActivationWriterAuthority.None, response.WriterAuthority);
        Assert.False(response.LeaseActive);
    }

    [Fact]
    public async Task Viewer_gets_sanitized_state_without_owner_evidence_or_fencing_token()
    {
        const string siteId = "site-safety-sanitized";
        var store = _factory.Services.GetRequiredService<IActivationWriterSafetyStore>();
        var now = _factory.Services.GetRequiredService<IClock>().UtcNow.ToUniversalTime();
        var initial = ActivationSafetyState.InitialFailClosed(
            siteId,
            now.AddMinutes(-2),
            "operator-private-id",
            "private initialization reason");
        Assert.True(await store.CompareExchangeStateAsync(initial, null, CancellationToken.None));
        var state = initial with
        {
            KillSwitchEngaged = false,
            WriterAuthority = ActivationWriterAuthority.ProductAgent,
            LegacyWriterStoppedAtUtc = now.AddMinutes(-1),
            LegacyStopEvidence = "private-pilot-runbook-evidence",
            Revision = 2,
            UpdatedAtUtc = now,
            Reason = "private cutover reason",
        };
        Assert.True(await store.CompareExchangeStateAsync(state, 1, CancellationToken.None));
        var lease = await store.TryAcquireLeaseAsync(
            siteId,
            "private-agent-instance-id",
            now,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.True(lease.Acquired);
        using var client = ViewerClient();

        var response = await client.GetAsync($"/agent/sites/{siteId}/writer-safety");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<Response>(TestJson.Options);
        Assert.NotNull(body);
        Assert.True(body!.Configured);
        Assert.False(body.KillSwitchEngaged);
        Assert.True(body.LegacyWriterStopProven);
        Assert.True(body.LeaseActive);
        Assert.Equal(2, body.SafetyRevision);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-pilot", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-agent", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operator-private", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fencingToken", raw, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient ViewerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            BatteryEmsApiFactory.ViewerToken);
        return client;
    }

    private sealed record Response(
        bool Configured,
        bool KillSwitchEngaged,
        ActivationWriterAuthority WriterAuthority,
        bool LegacyWriterStopProven,
        long? SafetyRevision,
        bool LeaseActive);
}
