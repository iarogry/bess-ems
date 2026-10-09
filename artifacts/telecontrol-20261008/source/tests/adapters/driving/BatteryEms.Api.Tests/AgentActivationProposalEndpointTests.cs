using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatteryEms.Application.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class AgentActivationProposalEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private readonly BatteryEmsApiFactory _factory;

    public AgentActivationProposalEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Viewer_cannot_create_activation_proposal()
    {
        using var client = Client(BatteryEmsApiFactory.ViewerToken);

        var response = await client.PostAsJsonAsync(
            "/agent/sites/site-viewer/activation-proposals/2026-09-24",
            new { reason = "not authorized" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ineligible_comparison_cannot_create_proposal()
    {
        using var client = Client(BatteryEmsApiFactory.OperatorToken);

        var response = await client.PostAsJsonAsync(
            "/agent/sites/site-without-equivalence/activation-proposals/2026-09-24",
            new { reason = "must fail closed" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Four_eyes_approval_creates_only_held_sanitized_outbox_reference()
    {
        var siteId = $"site-proposal-{Guid.NewGuid():N}";
        var deliveryDate = new DateOnly(2026, 9, 24);
        await SeedEligibleAsync(siteId, deliveryDate);
        using var proposer = Client(BatteryEmsApiFactory.OperatorToken);
        using var approver = Client(BatteryEmsApiFactory.SecondOperatorToken);
        using var viewer = Client(BatteryEmsApiFactory.ViewerToken);

        var createdResponse = await proposer.PostAsJsonAsync(
            $"/agent/sites/{siteId}/activation-proposals/{deliveryDate:yyyy-MM-dd}",
            new { reason = "bounded pilot candidate" });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<Response>(TestJson.Options);
        Assert.NotNull(created);
        Assert.Equal(ActivationProposalStatus.Pending, created!.Status);
        var rawCreated = await createdResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("payload_json", rawCreated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windows", rawCreated, StringComparison.OrdinalIgnoreCase);

        var selfApproval = await proposer.PostAsJsonAsync(
            $"/agent/activation-proposals/{created.ProposalId:D}/approve",
            new { reason = "self approval is forbidden" });
        Assert.Equal(HttpStatusCode.Conflict, selfApproval.StatusCode);

        var approvedResponse = await approver.PostAsJsonAsync(
            $"/agent/activation-proposals/{created.ProposalId:D}/approve",
            new { reason = "second operator reviewed" });
        approvedResponse.EnsureSuccessStatusCode();
        var approved = await approvedResponse.Content.ReadFromJsonAsync<Response>(TestJson.Options);
        Assert.NotNull(approved);
        Assert.Equal(ActivationProposalStatus.Approved, approved!.Status);
        Assert.Equal(BatteryEmsApiFactory.SecondOperatorId, approved.ReviewedBy);
        Assert.Equal(ActivationOutboxStatus.Held, approved.OutboxStatus);
        Assert.NotNull(approved.OutboxItemId);
        var rawApproved = await approvedResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("payload_json", rawApproved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intervals", rawApproved, StringComparison.OrdinalIgnoreCase);

        var getResponse = await viewer.GetAsync(
            $"/agent/activation-proposals/{created.ProposalId:D}");
        getResponse.EnsureSuccessStatusCode();
        var read = await getResponse.Content.ReadFromJsonAsync<Response>(TestJson.Options);
        Assert.NotNull(read);
        Assert.Equal(created.ProposalId, read!.ProposalId);
        Assert.Equal(approved.OutboxItemId, read.OutboxItemId);
    }

    private async Task SeedEligibleAsync(string siteId, DateOnly deliveryDate)
    {
        var store = _factory.Services.GetRequiredService<IShadowPlanComparisonStore>();
        var snapshot = Snapshot(siteId, deliveryDate);
        await store.PutSnapshotAsync(ShadowPlanSide.Legacy, snapshot, CancellationToken.None);
        await store.PutSnapshotAsync(ShadowPlanSide.Shadow, snapshot, CancellationToken.None);
        var id = Guid.NewGuid();
        await store.SaveComparisonAsync(
            new ShadowPlanComparisonRecord(
                id,
                id,
                siteId,
                deliveryDate,
                DateTimeOffset.UtcNow,
                true,
                true,
                true,
                []),
            CancellationToken.None);
    }

    private static ShadowPlanSnapshot Snapshot(string siteId, DateOnly deliveryDate)
    {
        var starts = new[] { "00:00", "01:00", "02:00", "03:00", "04:00", "05:00" };
        return new ShadowPlanSnapshot(
            siteId,
            deliveryDate,
            true,
            [],
            Enumerable.Range(1, 4)
                .Select(zone => new ShadowTouWindow(
                    $"Z{zone}",
                    starts.Select(start => new ShadowTouInterval(
                        start,
                        true,
                        false,
                        false,
                        0,
                        30,
                        290)).ToArray()))
                .ToArray());
    }

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed record Response(
        Guid ProposalId,
        ActivationProposalStatus Status,
        string? ReviewedBy,
        Guid? OutboxItemId,
        ActivationOutboxStatus? OutboxStatus);
}
