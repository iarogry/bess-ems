using BatteryEms.Application.Realtime;
using BatteryEms.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.Telecontrol;

public sealed partial class TelecontrolPollingService(TelecontrolClient client, IOptions<TelecontrolOptions> options,
    ISiteTelemetryStore siteStore, IChpTelemetryStore chpStore, TimeProvider timeProvider,
    ILogger<TelecontrolPollingService> logger) : BackgroundService
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Read-only provider boundary retries failures and publishes unavailable quality.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.PollIntervalSeconds), timeProvider);
        do
        {
            var now = timeProvider.GetUtcNow();
            ChpTelemetry reading;
            try { reading = await client.ReadAsync(now, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Log only the exception type: provider bodies/headers can contain credentials.
                LogFailure(exception.GetType().Name);
                reading = new ChpTelemetry(settings.AssetId, settings.DeviceId, now, now, null, null,
                    DataQuality.ProtocolError("telecontrol-poll-failed"), [], []);
            }
            chpStore.Update(reading);
            siteStore.Update(new SiteTelemetry(reading.Timestamp, reading.AssetId, reading.PowerKw,
                null, null, null, reading.Quality), reading.ReceivedAt);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telecontrol read-only poll failed ({ErrorType}).")]
    private partial void LogFailure(string errorType);
}
