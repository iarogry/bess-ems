using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.FusionSolar;

public sealed partial class FusionSolarSiteTelemetryHostedService : BackgroundService
{
    private readonly FusionSolarSiteTelemetrySource _source;
    private readonly FusionSolarOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FusionSolarSiteTelemetryHostedService> _logger;

    public FusionSolarSiteTelemetryHostedService(
        FusionSolarSiteTelemetrySource source,
        IOptions<FusionSolarOptions> options,
        TimeProvider timeProvider,
        ILogger<FusionSolarSiteTelemetryHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background adapter boundary must keep retrying after arbitrary FusionSolar/network/JSON failures.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.PollIntervalSeconds),
            _timeProvider);
        do
        {
            try
            {
                var updated = await _source
                    .PollAsync(_timeProvider.GetUtcNow(), stoppingToken)
                    .ConfigureAwait(false);
                LogPollSucceeded(updated);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "FusionSolar real-time plant cycle completed. Updated stations: {UpdatedStations}.")]
    private partial void LogPollSucceeded(int updatedStations);

    [LoggerMessage(Level = LogLevel.Error, Message = "FusionSolar real-time plant cycle failed.")]
    private partial void LogPollFailed(Exception exception);
}
