using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Persistence;
using BatteryEms.Application.Time;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ActivationCutoverWorkflowTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task In_memory_release_fails_closed_and_is_audited()
    {
        var audit = new InMemoryOperatorAuditLog();
        var useCase = new DefaultActivationCutoverUseCase(
            new FailClosedActivationCutoverStore(),
            audit,
            new FixedClock());

        var result = await useCase.ReleaseAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "agent-instance-a",
            expectedSafetyRevision: 3,
            expectedFencingToken: 7,
            "operator-b",
            "controlled release",
            CancellationToken.None);

        Assert.False(result.Released);
        Assert.Equal("activation-durable-persistence-required", result.ErrorCode);
        var entry = Assert.Single(await audit.QueryAsync(
            Now.AddMinutes(-1),
            Now.AddMinutes(1),
            CancellationToken.None));
        Assert.Equal("activation-outbox-release", entry.Action);
        Assert.Equal("activation-durable-persistence-required", entry.Outcome);
    }

    [Fact]
    public async Task In_memory_rollback_fails_closed_and_is_audited()
    {
        var audit = new InMemoryOperatorAuditLog();
        var useCase = new DefaultActivationCutoverUseCase(
            new FailClosedActivationCutoverStore(),
            audit,
            new FixedClock());

        var result = await useCase.RollbackAsync(
            Guid.NewGuid(),
            "site-1",
            expectedSafetyRevision: 3,
            "operator-b",
            "emergency rollback",
            CancellationToken.None);

        Assert.False(result.RolledBack);
        Assert.Equal("activation-durable-persistence-required", result.ErrorCode);
        var entry = Assert.Single(await audit.QueryAsync(
            Now.AddMinutes(-1),
            Now.AddMinutes(1),
            CancellationToken.None));
        Assert.Equal("activation-writer-rollback", entry.Action);
        Assert.Equal("site-1", entry.TargetAssetId);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
