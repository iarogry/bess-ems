using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatteryEms.Application.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class AgentPilotReadinessEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private readonly BatteryEmsApiFactory _factory;

    public AgentPilotReadinessEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_request_is_rejected()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/agent/sites/site-pilot-anonymous/pilot-readiness?windowEnd=2026-09-24");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_evidence_is_reported_fail_closed_for_all_required_days()
    {
        using var client = ViewerClient();

        var response = await client.GetFromJsonAsync<Response>(
            "/agent/sites/site-pilot-missing/pilot-readiness?windowEnd=2026-09-24",
            TestJson.Options);

        Assert.NotNull(response);
        Assert.False(response!.IsReady);
        Assert.Equal(14, response.RequiredDays);
        Assert.Equal(0, response.PassingDays);
        Assert.Contains("pilot-evidence-missing-day", response.BlockingCodes);
        Assert.Equal(14, response.Days.Count);
    }

    [Fact]
    public async Task Viewer_can_read_sanitized_passing_window()
    {
        const string siteId = "site-pilot-ready";
        var end = new DateOnly(2026, 9, 24);
        var store = _factory.Services.GetRequiredService<IShadowPlanComparisonStore>();
        for (var offset = 13; offset >= 0; offset--)
        {
            var date = end.AddDays(-offset);
            await store.SaveComparisonAsync(
                new ShadowPlanComparisonRecord(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    siteId,
                    date,
                    new DateTimeOffset(date.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero),
                    IsEquivalent: true,
                    LegacyPayloadReady: true,
                    ShadowPayloadReady: true,
                    Mismatches: []),
                CancellationToken.None);
        }
        using var client = ViewerClient();

        var response = await client.GetAsync(
            $"/agent/sites/{siteId}/pilot-readiness?windowEnd=2026-09-24");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<Response>(TestJson.Options);
        Assert.NotNull(body);
        Assert.True(body!.IsReady);
        Assert.Equal(14, body.PassingDays);
        Assert.Empty(body.BlockingCodes);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("legacy_value", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shadow_value", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mismatches", raw, StringComparison.OrdinalIgnoreCase);
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
        int RequiredDays,
        int PassingDays,
        bool IsReady,
        IReadOnlyList<string> BlockingCodes,
        IReadOnlyList<Day> Days);

    private sealed record Day(DateOnly DeliveryDate, bool EvidencePresent, bool Passed, string? BlockingCode);
}
