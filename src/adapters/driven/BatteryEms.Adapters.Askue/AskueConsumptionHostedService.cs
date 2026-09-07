using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.Askue;

public sealed partial class AskueConsumptionHostedService : BackgroundService
{
    private readonly AskueSiteConsumptionCollector _collector;
    private readonly AskueOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AskueConsumptionHostedService> _logger;

    public AskueConsumptionHostedService(
        AskueSiteConsumptionCollector collector,
        IOptions<AskueOptions> options,
        TimeProvider timeProvider,
        ILogger<AskueConsumptionHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _collector = collector;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background ASKUE adapter boundary must keep retrying after protocol/network failures.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollIntervalSeconds), _timeProvider);
        do
        {
            try
            {
                var timeZone = AskueSiteConsumptionCollector.ResolveTimeZone(_options.TimeZoneId);
                var localNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZone);
                var targetDate = DateOnly.FromDateTime(localNow.DateTime.AddDays(-_options.DaysBack));
                var count = await _collector.CollectAsync(targetDate, stoppingToken).ConfigureAwait(false);
                LogPollSucceeded(count, targetDate);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPollFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ASKUE site consumption poll completed for {TargetDate}. Imported readings: {ImportedReadings}.")]
    private partial void LogPollSucceeded(int importedReadings, DateOnly targetDate);

    [LoggerMessage(Level = LogLevel.Error, Message = "ASKUE site consumption poll failed.")]
    private partial void LogPollFailed(Exception exception);
}
