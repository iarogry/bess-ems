using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Persistence;
using BatteryEms.Application.Time;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ActivationPilotSessionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Session_is_active_only_inside_armed_window()
    {
        var session = Session();

        Assert.True(session.IsActiveAt(Now));
        Assert.True(session.IsActiveAt(Now.AddMinutes(14)));
        Assert.False(session.IsActiveAt(Now.AddMinutes(15)));
        Assert.False((session with
        {
            Status = ActivationPilotSessionStatus.Aborted,
            AbortedBy = "operator-d",
            AbortReason = "stop",
            AbortedAtUtc = Now.AddMinutes(1),
        }).EnsureValid().IsActiveAt(Now.AddMinutes(2)));
    }

    [Fact]
    public void Session_cannot_exceed_fifteen_minutes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (Session() with { ExpiresAtUtc = Now.AddMinutes(16) }).EnsureValid());
    }

    [Fact]
    public void Session_requires_one_explicit_known_window()
    {
        Assert.Throws<ArgumentException>(() =>
            (Session() with { WindowId = "Z5" }).EnsureValid());
    }

    [Fact]
    public void Abort_metadata_must_exactly_match_aborted_status()
    {
        Assert.Throws<ArgumentException>(() =>
            (Session() with { Status = ActivationPilotSessionStatus.Aborted }).EnsureValid());
        Assert.Throws<ArgumentException>(() =>
            (Session() with { AbortedBy = "operator-d" }).EnsureValid());
    }

    [Fact]
    public void Ready_outbox_requires_pilot_session_in_complete_release_metadata()
    {
        var item = new ActivationOutboxItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "site-1",
            new DateOnly(2026, 9, 26),
            "hash",
            "{}",
            "activation:test",
            ActivationOutboxStatus.Ready,
            Now,
            UpdatedAtUtc: Now,
            ReleaseWriterOwnerId: "agent-instance-a",
            ReleaseSafetyRevision: 4,
            ReleaseFencingToken: 8,
            ReleasedBy: "operator-d",
            ReleaseReason: "bounded release",
            ReleasedAtUtc: Now);

        Assert.Throws<ArgumentException>(() => item.EnsureValid());
        Assert.NotNull((item with
        {
            ReleasePilotSessionId = Guid.NewGuid(),
            ReleaseWindowId = "Z1",
        }).EnsureValid());
    }

    [Fact]
    public async Task In_memory_store_fails_closed_and_arm_is_audited()
    {
        var audit = new InMemoryOperatorAuditLog();
        var useCase = new DefaultActivationPilotSessionUseCase(
            new FailClosedActivationPilotSessionStore(),
            audit,
            new FixedClock());

        var result = await useCase.ArmAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Z1",
            "agent-instance-a",
            4,
            8,
            "operator-c",
            "bounded pilot",
            CancellationToken.None);

        Assert.False(result.Armed);
        Assert.Equal("activation-durable-persistence-required", result.ErrorCode);
        var entry = Assert.Single(await audit.QueryAsync(
            Now.AddMinutes(-1),
            Now.AddMinutes(1),
            CancellationToken.None));
        Assert.Equal("activation-pilot-arm", entry.Action);
        Assert.Equal("activation-durable-persistence-required", entry.Outcome);
    }

    private static ActivationPilotSession Session() => new ActivationPilotSession(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "site-1",
        "Z1",
        "agent-instance-a",
        4,
        8,
        ActivationPilotSessionStatus.Armed,
        "operator-c",
        "bounded pilot",
        Now,
        Now.AddMinutes(15)).EnsureValid();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
