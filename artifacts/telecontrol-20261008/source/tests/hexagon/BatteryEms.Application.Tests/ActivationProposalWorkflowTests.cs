using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Persistence;
using BatteryEms.Application.Time;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ActivationProposalWorkflowTests
{
    [Fact]
    public async Task Only_equivalent_ready_comparison_can_create_idempotent_proposal()
    {
        var fixture = new Fixture();
        await fixture.SeedEligibleComparisonAsync();

        var first = await fixture.UseCase.ProposeAsync(
            Fixture.SiteId,
            Fixture.DeliveryDate,
            "operator-1",
            "pilot candidate",
            CancellationToken.None);
        var second = await fixture.UseCase.ProposeAsync(
            Fixture.SiteId,
            Fixture.DeliveryDate,
            "operator-1",
            "repeat",
            CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.NotNull(first.Proposal);
        Assert.Equal(first.Proposal!.ProposalId, second.Proposal!.ProposalId);
        Assert.Equal(ActivationProposalStatus.Pending, first.Proposal.Status);
        Assert.Equal(64, first.Proposal.PayloadHash.Length);
        Assert.Contains("Windows", first.Proposal.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_equivalent_comparison_is_rejected_and_audited()
    {
        var fixture = new Fixture();
        await fixture.Comparisons.SaveComparisonAsync(
            Comparison(isEquivalent: false, [new ShadowPlanMismatch("power", "1", "2")]),
            CancellationToken.None);

        var result = await fixture.UseCase.ProposeAsync(
            Fixture.SiteId,
            Fixture.DeliveryDate,
            "operator-1",
            "must not pass",
            CancellationToken.None);

        Assert.Null(result.Proposal);
        Assert.Equal("activation-comparison-not-eligible", result.ErrorCode);
        var audit = await fixture.Audit.QueryAsync(
            fixture.Clock.UtcNow.AddMinutes(-1),
            fixture.Clock.UtcNow.AddMinutes(1),
            CancellationToken.None);
        Assert.Contains(audit, item => item.Outcome == "activation-comparison-not-eligible");
    }

    [Fact]
    public async Task Approval_requires_second_operator_and_creates_only_held_outbox()
    {
        var fixture = new Fixture();
        await fixture.SeedEligibleComparisonAsync();
        var created = await fixture.UseCase.ProposeAsync(
            Fixture.SiteId,
            Fixture.DeliveryDate,
            "operator-1",
            "pilot candidate",
            CancellationToken.None);

        var selfApproval = await fixture.UseCase.ApproveAsync(
            created.Proposal!.ProposalId,
            "operator-1",
            "self approval",
            CancellationToken.None);
        var approved = await fixture.UseCase.ApproveAsync(
            created.Proposal.ProposalId,
            "operator-2",
            "four eyes accepted",
            CancellationToken.None);
        var replay = await fixture.UseCase.ApproveAsync(
            created.Proposal.ProposalId,
            "operator-2",
            "retry",
            CancellationToken.None);

        Assert.False(selfApproval.Approved);
        Assert.Equal("activation-four-eyes-required", selfApproval.ErrorCode);
        Assert.True(approved.Approved);
        Assert.Equal(ActivationProposalStatus.Approved, approved.Proposal!.Status);
        Assert.Equal("operator-2", approved.Proposal.ReviewedBy);
        Assert.NotNull(approved.OutboxItem);
        Assert.Equal(ActivationOutboxStatus.Held, approved.OutboxItem!.Status);
        Assert.Equal(approved.OutboxItem.OutboxItemId, replay.OutboxItem!.OutboxItemId);
        Assert.Equal(approved.Proposal.PayloadHash, approved.OutboxItem.PayloadHash);
        Assert.Equal(approved.Proposal.PayloadJson, approved.OutboxItem.PayloadJson);
    }

    [Fact]
    public async Task Expired_proposal_cannot_create_outbox()
    {
        var fixture = new Fixture();
        await fixture.SeedEligibleComparisonAsync();
        var created = await fixture.UseCase.ProposeAsync(
            Fixture.SiteId,
            Fixture.DeliveryDate,
            "operator-1",
            "pilot candidate",
            CancellationToken.None);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(31);

        var approval = await fixture.UseCase.ApproveAsync(
            created.Proposal!.ProposalId,
            "operator-2",
            "too late",
            CancellationToken.None);

        Assert.False(approval.Approved);
        Assert.Null(approval.OutboxItem);
        Assert.Equal("activation-proposal-expired", approval.ErrorCode);
        Assert.Equal(ActivationProposalStatus.Expired, approval.Proposal!.Status);
    }

    private sealed class Fixture
    {
        public const string SiteId = "site-proposal";
        public static readonly DateOnly DeliveryDate = new(2026, 9, 24);
        public InMemoryShadowPlanComparisonStore Comparisons { get; } = new();
        public InMemoryOperatorAuditLog Audit { get; } = new();
        public MutableClock Clock { get; } = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        public DefaultActivationProposalUseCase UseCase { get; }

        public Fixture()
        {
            UseCase = new DefaultActivationProposalUseCase(
                Comparisons,
                new InMemoryActivationProposalStore(),
                Audit,
                Clock);
        }

        public async Task SeedEligibleComparisonAsync()
        {
            await Comparisons.PutSnapshotAsync(
                ShadowPlanSide.Shadow,
                Snapshot(),
                CancellationToken.None);
            await Comparisons.SaveComparisonAsync(
                Comparison(isEquivalent: true, []),
                CancellationToken.None);
        }
    }

    private static ShadowPlanComparisonRecord Comparison(
        bool isEquivalent,
        IReadOnlyList<ShadowPlanMismatch> mismatches)
    {
        var id = Guid.NewGuid();
        return new ShadowPlanComparisonRecord(
            id,
            id,
            Fixture.SiteId,
            Fixture.DeliveryDate,
            new DateTimeOffset(2026, 9, 23, 11, 0, 0, TimeSpan.Zero),
            isEquivalent,
            true,
            true,
            mismatches);
    }

    private static ShadowPlanSnapshot Snapshot()
    {
        var starts = new[] { "00:00", "01:00", "02:00", "03:00", "04:00", "05:00" };
        return new ShadowPlanSnapshot(
            Fixture.SiteId,
            Fixture.DeliveryDate,
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

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
