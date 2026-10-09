using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatteryEms.Application.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class AgentShadowEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private readonly BatteryEmsApiFactory _factory;

    public AgentShadowEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_request_is_rejected()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/agent/sites/site-anonymous/shadow-comparisons/latest");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_sanitized_latest_comparison()
    {
        var comparison = new ShadowPlanComparisonRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "site-agent-viewer",
            new DateOnly(2026, 9, 23),
            new DateTimeOffset(2026, 9, 22, 15, 0, 0, TimeSpan.Zero),
            false,
            true,
            true,
            [new ShadowPlanMismatch("windows[0].intervals[1].power_watts", "80000", "79990")]);
        var store = _factory.Services.GetRequiredService<IShadowPlanComparisonStore>();
        await store.SaveComparisonAsync(comparison, CancellationToken.None);
        using var client = ViewerClient();

        var response = await client.GetAsync(
            "/agent/sites/site-agent-viewer/shadow-comparisons/latest?deliveryDate=2026-09-23");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<Response>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(comparison.ComparisonId, body!.ComparisonId);
        Assert.False(body.IsEquivalent);
        var mismatch = Assert.Single(body.Mismatches);
        Assert.Equal("windows[0].intervals[1].power_watts", mismatch.Path);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("windows\"", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("device", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_delivery_date_is_rejected_for_authenticated_viewer()
    {
        using var client = ViewerClient();

        var response = await client.GetAsync(
            "/agent/sites/site-agent-viewer/shadow-comparisons/latest?deliveryDate=23-09-2026");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_comparison_returns_not_found_without_synthesizing_data()
    {
        using var client = ViewerClient();

        var response = await client.GetAsync(
            "/agent/sites/site-no-shadow-result/shadow-comparisons/latest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_cannot_start_shadow_run()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/agent/sites/site-shadow-control/shadow-runs/2026-09-23",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_cannot_start_shadow_run()
    {
        using var client = ViewerClient();

        var response = await client.PostAsync(
            "/agent/sites/site-shadow-control/shadow-runs/2026-09-23",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Operator_starts_only_idempotent_shadow_run()
    {
        using var client = OperatorClient();

        var first = await client.PostAsync(
            "/agent/sites/site-shadow-control/shadow-runs/2026-09-23",
            content: null);
        var second = await client.PostAsync(
            "/agent/sites/site-shadow-control/shadow-runs/2026-09-23",
            content: null);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<RunResponse>(TestJson.Options);
        var secondBody = await second.Content.ReadFromJsonAsync<RunResponse>(TestJson.Options);
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.Equal(firstBody!.RunId, secondBody!.RunId);
        Assert.Equal("site-shadow-control", firstBody.SiteId);
        Assert.Equal(new DateOnly(2026, 9, 23), firstBody.DeliveryDate);
        Assert.Equal(OrchestrationRunStatus.Blocked, firstBody.Status);
        Assert.DoesNotContain("device", firstBody.ComparisonUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Operator_shadow_run_rejects_non_iso_date()
    {
        using var client = OperatorClient();

        var response = await client.PostAsync(
            "/agent/sites/site-shadow-control/shadow-runs/23-09-2026",
            content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient ViewerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            BatteryEmsApiFactory.ViewerToken);
        return client;
    }

    private HttpClient OperatorClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            BatteryEmsApiFactory.OperatorToken);
        return client;
    }

    private sealed record Response(
        Guid ComparisonId,
        bool IsEquivalent,
        IReadOnlyList<Mismatch> Mismatches);

    private sealed record Mismatch(string Path, string LegacyValue, string ShadowValue);

    private sealed record RunResponse(
        Guid RunId,
        string SiteId,
        DateOnly DeliveryDate,
        OrchestrationRunStatus Status,
        string ComparisonUrl);
}
