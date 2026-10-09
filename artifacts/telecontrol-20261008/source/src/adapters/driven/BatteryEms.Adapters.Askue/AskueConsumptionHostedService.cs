using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BatteryEms.Application.Site;
using System.Net;

namespace BatteryEms.Adapters.Askue;

public sealed partial class AskueConsumptionHostedService : BackgroundService
{
    private readonly AskueSiteConsumptionCollector _collector;
    private readonly AskueOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AskueConsumptionHostedService> _logger;
    private readonly ISiteConsumptionPollStatusStore? _statuses;

    public AskueConsumptionHostedService(
        AskueSiteConsumptionCollector collector,
        IOptions<AskueOptions> options,
        TimeProvider timeProvider,
        ILogger<AskueConsumptionHostedService> logger,
        ISiteConsumptionPollStatusStore? statuses = null)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _collector = collector;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _statuses = statuses;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background ASKUE adapter boundary must keep retrying after protocol/network failures.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollIntervalSeconds), _timeProvider);
        do
        {
            try
            {
                _statuses?.Started(_options.AccountId, _options.SiteId, _timeProvider.GetUtcNow());
                var timeZone = AskueSiteConsumptionCollector.ResolveTimeZone(_options.TimeZoneId);
                var localNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZone);
                var targetDate = DateOnly.FromDateTime(localNow.DateTime.AddDays(-_options.DaysBack));
                var count = await _collector.CollectAsync(targetDate, stoppingToken).ConfigureAwait(false);
                _statuses?.Succeeded(_options.AccountId, _options.SiteId, _timeProvider.GetUtcNow(), count);
                LogPollSucceeded(_options.AccountId, _options.SiteId, count, targetDate);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var code = ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
                    ? "authentication_failed" : ex is HttpRequestException ? "http_error" : "poll_failed";
                _statuses?.Failed(_options.AccountId, _options.SiteId, _timeProvider.GetUtcNow(), code);
                LogPollFailed(ex, _options.AccountId, _options.SiteId);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ASKUE account {AccountId}, site {SiteId}: poll completed for {TargetDate}. Imported readings: {ImportedReadings}.")]
    private partial void LogPollSucceeded(string accountId, string siteId, int importedReadings, DateOnly targetDate);

    [LoggerMessage(Level = LogLevel.Error, Message = "ASKUE account {AccountId}, site {SiteId}: poll failed.")]
    private partial void LogPollFailed(Exception exception, string accountId, string siteId);
}
