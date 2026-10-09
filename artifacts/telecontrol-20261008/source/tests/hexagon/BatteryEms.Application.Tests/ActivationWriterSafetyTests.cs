using BatteryEms.Application.Orchestration;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ActivationWriterSafetyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Missing_state_and_default_state_fail_closed()
    {
        var missing = ActivationWriterSafetyGate.Evaluate(
            "site-1",
            "agent-a",
            safetyState: null,
            lease: null,
            Now);
        var initial = ActivationSafetyState.InitialFailClosed(
            "site-1",
            Now,
            "operator-a",
            "initialize safety controls");
        var defaultState = ActivationWriterSafetyGate.Evaluate(
            "site-1",
            "agent-a",
            initial,
            lease: null,
            Now);

        Assert.False(missing.CanWrite);
        Assert.Equal("activation-safety-state-missing", missing.BlockingCode);
        Assert.False(defaultState.CanWrite);
        Assert.Equal("activation-kill-switch-engaged", defaultState.BlockingCode);
        Assert.Equal(ActivationWriterAuthority.LegacyRunner, initial.WriterAuthority);
    }

    [Fact]
    public void Product_authority_requires_legacy_stop_evidence()
    {
        var invalid = new ActivationSafetyState(
            "site-1",
            KillSwitchEngaged: true,
            ActivationWriterAuthority.ProductAgent,
            LegacyWriterStoppedAtUtc: null,
            LegacyStopEvidence: null,
            Revision: 2,
            Now,
            "operator-a",
            "attempt cutover");

        var error = Assert.Throws<ArgumentException>(() => invalid.EnsureValid());

        Assert.Contains("legacy-writer stop evidence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Safety_state_update_uses_revision_compare_exchange()
    {
        var store = new InMemoryActivationWriterSafetyStore();
        var initial = ActivationSafetyState.InitialFailClosed(
            "site-1",
            Now,
            "operator-a",
            "initialize");

        Assert.True(await store.CompareExchangeStateAsync(initial, null, CancellationToken.None));
        Assert.False(await store.CompareExchangeStateAsync(
            initial with { Revision = 2, Reason = "stale update" },
            expectedRevision: 0,
            CancellationToken.None));
        Assert.True(await store.CompareExchangeStateAsync(
            initial with
            {
                Revision = 2,
                UpdatedAtUtc = Now.AddMinutes(1),
                Reason = "engage remains explicit",
            },
            expectedRevision: 1,
            CancellationToken.None));
        Assert.Equal(2, (await store.FindStateAsync("site-1", CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task Lease_allows_one_owner_and_increments_fence_after_expiry()
    {
        var store = new InMemoryActivationWriterSafetyStore();
        var first = await store.TryAcquireLeaseAsync(
            "site-1",
            "agent-a",
            Now,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        var blocked = await store.TryAcquireLeaseAsync(
            "site-1",
            "agent-b",
            Now.AddSeconds(10),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        var successor = await store.TryAcquireLeaseAsync(
            "site-1",
            "agent-b",
            Now.AddMinutes(2),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.True(first.Acquired);
        Assert.False(blocked.Acquired);
        Assert.Equal("activation-writer-lease-owned-by-other", blocked.ErrorCode);
        Assert.True(successor.Acquired);
        Assert.True(successor.Lease!.FencingToken > first.Lease!.FencingToken);
    }

    [Fact]
    public async Task Gate_opens_only_for_authorized_owner_with_live_fenced_lease()
    {
        var store = new InMemoryActivationWriterSafetyStore();
        var leaseResult = await store.TryAcquireLeaseAsync(
            "site-1",
            "agent-a",
            Now,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        var state = new ActivationSafetyState(
            "site-1",
            KillSwitchEngaged: false,
            ActivationWriterAuthority.ProductAgent,
            LegacyWriterStoppedAtUtc: Now.AddMinutes(-1),
            LegacyStopEvidence: "pilot-runbook-step-7",
            Revision: 3,
            Now,
            "operator-b",
            "controlled pilot cutover").EnsureValid();

        var allowed = ActivationWriterSafetyGate.Evaluate(
            "site-1",
            "agent-a",
            state,
            leaseResult.Lease,
            Now.AddSeconds(10));
        var wrongOwner = ActivationWriterSafetyGate.Evaluate(
            "site-1",
            "agent-b",
            state,
            leaseResult.Lease,
            Now.AddSeconds(10));
        var expired = ActivationWriterSafetyGate.Evaluate(
            "site-1",
            "agent-a",
            state,
            leaseResult.Lease,
            Now.AddMinutes(1));

        Assert.True(allowed.CanWrite);
        Assert.Equal(leaseResult.Lease!.FencingToken, allowed.FencingToken);
        Assert.Equal(state.Revision, allowed.SafetyRevision);
        Assert.False(wrongOwner.CanWrite);
        Assert.Equal("activation-writer-lease-owned-by-other", wrongOwner.BlockingCode);
        Assert.False(expired.CanWrite);
        Assert.Equal("activation-writer-lease-expired", expired.BlockingCode);
    }
}
