using BatteryEms.Application.Orchestration;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ShadowRunTriggerTests
{
    [Fact]
    public async Task Creates_bounded_day_ahead_command_with_kyiv_horizon_and_stable_key()
    {
        var orchestration = new CapturingOrchestrationUseCase();
        var trigger = new DefaultShadowRunTrigger(orchestration);

        await trigger.StartAsync(
            " site-shadow ",
            new DateOnly(2026, 9, 23),
            OrchestrationTriggerType.Manual,
            CancellationToken.None);

        var command = Assert.IsType<OrchestrationStartCommand>(orchestration.Command);
        Assert.Equal("site-shadow", command.SiteId);
        Assert.Equal("day-ahead-shadow", command.RunType);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 21, 0, 0, TimeSpan.Zero), command.HorizonStart);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 21, 0, 0, TimeSpan.Zero), command.HorizonEnd);
        Assert.Equal(OrchestrationTriggerType.Manual, command.TriggerType);
        Assert.Equal("2026-09-23", command.TriggerRef);
        Assert.Equal("day-ahead-shadow:site-shadow:2026-09-23", command.IdempotencyKey);
    }

    [Fact]
    public async Task Rejects_source_event_so_the_tool_cannot_expand_beyond_bounded_triggers()
    {
        var trigger = new DefaultShadowRunTrigger(new CapturingOrchestrationUseCase());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => trigger.StartAsync(
            "site-shadow",
            new DateOnly(2026, 9, 23),
            OrchestrationTriggerType.SourceEvent,
            CancellationToken.None));
    }

    private sealed class CapturingOrchestrationUseCase : IOrchestrationUseCase
    {
        public OrchestrationStartCommand? Command { get; private set; }

        public Task<OrchestrationRun> StartAsync(
            OrchestrationStartCommand command,
            CancellationToken cancellationToken)
        {
            Command = command;
            return Task.FromResult(new OrchestrationRun(
                Guid.NewGuid(),
                command.SiteId,
                command.RunType,
                command.HorizonStart,
                command.HorizonEnd,
                command.TriggerType,
                command.TriggerRef,
                command.IdempotencyKey!,
                OrchestrationRunStatus.Succeeded,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                command.InputHash,
                OutputRef: null,
                ErrorCode: null,
                ErrorMessage: null,
                MetadataJson: "{}"));
        }
    }
}
