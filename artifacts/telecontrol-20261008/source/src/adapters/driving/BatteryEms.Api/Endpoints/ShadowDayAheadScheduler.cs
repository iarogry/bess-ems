using BatteryEms.Application.Markets;
using BatteryEms.Application.Orchestration;
using BatteryEms.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BatteryEms.Api.Scheduling;

public sealed record ShadowDayAheadSchedulerOptions(
    string SiteId,
    int TriggerHourLocal,
    int TriggerMinuteLocal,
    int PollIntervalSeconds = 60)
{
    public ShadowDayAheadSchedulerOptions EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentOutOfRangeException.ThrowIfNegative(TriggerHourLocal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TriggerHourLocal, 23);
        ArgumentOutOfRangeException.ThrowIfNegative(TriggerMinuteLocal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TriggerMinuteLocal, 59);
        ArgumentOutOfRangeException.ThrowIfLessThan(PollIntervalSeconds, 10);
        return this;
    }
}

public enum ShadowSchedulerTickStatus
{
    LegacyUnavailable,
    ShadowScheduleUnavailable,
    Started,
    AlreadyStarted,
}

public sealed record ShadowSchedulerTickResult(
    ShadowSchedulerTickStatus Status,
    DateOnly DeliveryDate,
    Guid? RunId = null,
    OrchestrationRunStatus? RunStatus = null,
    string? ReasonCode = null);

/// <summary>
/// Deterministic scheduler core. Readiness probes happen before consuming the
/// orchestration idempotency key, so late inputs can be picked up by a later
/// poll without creating a permanently blocked run.
/// </summary>
public sealed class ShadowDayAheadScheduler
{
    private readonly ShadowDayAheadSchedulerOptions _options;
    private readonly ShadowDeyeProjectionOptions _projectionOptions;
    private readonly ILegacyPlanSnapshotSource _legacy;
    private readonly IScheduleRepository _schedules;
    private readonly IShadowRunTrigger _trigger;
    private DateOnly? _lastStartedDeliveryDate;

    public ShadowDayAheadScheduler(
        ShadowDayAheadSchedulerOptions options,
        ShadowDeyeProjectionOptions projectionOptions,
        ILegacyPlanSnapshotSource legacy,
        IScheduleRepository schedules,
        IShadowRunTrigger trigger)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).EnsureValid();
        _projectionOptions = (projectionOptions
            ?? throw new ArgumentNullException(nameof(projectionOptions))).EnsureValid();
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
    }

    public async Task<ShadowSchedulerTickResult> RunOnceAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var deliveryDate = ResolveDeliveryDate(utcNow, _options);
        if (_lastStartedDeliveryDate == deliveryDate)
        {
            return new ShadowSchedulerTickResult(
                ShadowSchedulerTickStatus.AlreadyStarted,
                deliveryDate);
        }

        var legacy = await _legacy.LoadAsync(
            _options.SiteId,
            deliveryDate,
            cancellationToken).ConfigureAwait(false);
        if (!legacy.IsSuccess)
        {
            return new ShadowSchedulerTickResult(
                ShadowSchedulerTickStatus.LegacyUnavailable,
                deliveryDate,
                ReasonCode: legacy.ErrorCode ?? "legacy-scenario-unavailable");
        }

        var projection = ScheduleShadowPlanProjector.Project(
            _options.SiteId,
            deliveryDate,
            _schedules.FindActive(_options.SiteId, ScheduleType.DayAhead),
            _projectionOptions);
        if (!projection.PayloadReady)
        {
            return new ShadowSchedulerTickResult(
                ShadowSchedulerTickStatus.ShadowScheduleUnavailable,
                deliveryDate,
                ReasonCode: projection.BlockingCodes[0]);
        }

        var run = await _trigger.StartAsync(
            _options.SiteId,
            deliveryDate,
            OrchestrationTriggerType.Scheduled,
            cancellationToken).ConfigureAwait(false);
        _lastStartedDeliveryDate = deliveryDate;
        return new ShadowSchedulerTickResult(
            ShadowSchedulerTickStatus.Started,
            deliveryDate,
            run.RunId,
            run.Status);
    }

    internal static DateOnly ResolveDeliveryDate(
        DateTimeOffset utcNow,
        ShadowDayAheadSchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var local = TimeZoneInfo.ConvertTime(utcNow, ResolveKyivTimeZone());
        var trigger = new TimeOnly(options.TriggerHourLocal, options.TriggerMinuteLocal);
        return DateOnly.FromDateTime(local.DateTime)
            .AddDays(TimeOnly.FromDateTime(local.DateTime) >= trigger ? 1 : 0);
    }

    private static TimeZoneInfo ResolveKyivTimeZone()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Kyiv", out var timeZone)
            || TimeZoneInfo.TryFindSystemTimeZoneById("FLE Standard Time", out timeZone))
        {
            return timeZone;
        }

        throw new InvalidOperationException("Europe/Kyiv time zone is unavailable.");
    }
}

public sealed partial class ShadowDayAheadSchedulerHostedService : BackgroundService
{
    private readonly ShadowDayAheadScheduler _scheduler;
    private readonly ShadowDayAheadSchedulerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ShadowDayAheadSchedulerHostedService> _logger;

    public ShadowDayAheadSchedulerHostedService(
        ShadowDayAheadScheduler scheduler,
        ShadowDayAheadSchedulerOptions options,
        TimeProvider timeProvider,
        ILogger<ShadowDayAheadSchedulerHostedService> logger)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A shadow scheduler must remain alive and retry after read-only input or persistence failures.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.PollIntervalSeconds),
            _timeProvider);
        do
        {
            try
            {
                var result = await _scheduler.RunOnceAsync(
                    _timeProvider.GetUtcNow(),
                    stoppingToken).ConfigureAwait(false);
                if (result.Status == ShadowSchedulerTickStatus.Started)
                {
                    LogStarted(result.DeliveryDate, result.RunId, result.RunStatus);
                }
                else
                {
                    LogWaiting(result.Status, result.DeliveryDate, result.ReasonCode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Shadow scheduler started delivery_date={DeliveryDate} run_id={RunId} run_status={RunStatus}.")]
    private partial void LogStarted(
        DateOnly deliveryDate,
        Guid? runId,
        OrchestrationRunStatus? runStatus);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Shadow scheduler waiting status={Status} delivery_date={DeliveryDate} reason={ReasonCode}.")]
    private partial void LogWaiting(
        ShadowSchedulerTickStatus status,
        DateOnly deliveryDate,
        string? reasonCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shadow scheduler tick failed; the next poll will retry.")]
    private partial void LogFailure(Exception exception);
}
