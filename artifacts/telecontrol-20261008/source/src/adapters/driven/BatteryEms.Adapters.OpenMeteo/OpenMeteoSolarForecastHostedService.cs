using BatteryEms.Application.Forecasting;
using BatteryEms.Application.Site;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.OpenMeteo;

public sealed partial class OpenMeteoSolarForecastHostedService : BackgroundService
{
    private readonly ISolarForecastProvider _source;
    private readonly ISolarForecastStore _store;
    private readonly ISitePvProfileStore _pvProfiles;
    private readonly OpenMeteoSolarForecastOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenMeteoSolarForecastHostedService> _logger;

    public OpenMeteoSolarForecastHostedService(
        ISolarForecastProvider source,
        ISolarForecastStore store,
        ISitePvProfileStore pvProfiles,
        IOptions<OpenMeteoSolarForecastOptions> options,
        TimeProvider timeProvider,
        ILogger<OpenMeteoSolarForecastHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pvProfiles);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _store = store;
        _pvProfiles = pvProfiles;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background forecast refresh must keep retrying after weather/network/provider failures.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.ForecastRefreshSeconds), _timeProvider);
        do
        {
            try
            {
                var profiles = await _pvProfiles.ListEnabledAsync(stoppingToken).ConfigureAwait(false);
                if (profiles.Count > 0)
                {
                    await RefreshProfilesAsync(profiles, stoppingToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(_options.AssetId))
                {
                    var forecast = await _source.LoadAsync(stoppingToken).ConfigureAwait(false);
                    _store.Update(forecast);
                    LogRefreshSucceeded(forecast.AssetId, forecast.HorizonStart, forecast.HorizonEnd, forecast.Points.Count);
                }
                else
                {
                    LogSkippedNoProfiles();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRefreshFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RefreshProfilesAsync(
        IReadOnlyList<SitePvProfile> profiles,
        CancellationToken cancellationToken)
    {
        foreach (var profile in profiles)
        {
            if (!string.Equals(profile.ForecastProvider, "open_meteo", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(profile.ForecastProvider, "open-meteo", StringComparison.OrdinalIgnoreCase))
            {
                LogSkippedProvider(profile.SiteId, profile.PvSystemId, profile.ForecastProvider);
                continue;
            }

            var profileOptions = _options.CreateForSitePvProfile(profile);
            var forecast = await _source.LoadAsync(profileOptions, cancellationToken).ConfigureAwait(false);
            _store.Update(forecast);
            LogRefreshSucceeded(forecast.AssetId, forecast.HorizonStart, forecast.HorizonEnd, forecast.Points.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Open-Meteo solar forecast stored for asset '{AssetId}'. Horizon={HorizonStart:O}..{HorizonEnd:O}, Points={PointCount}.")]
    private partial void LogRefreshSucceeded(string assetId, DateTimeOffset horizonStart, DateTimeOffset horizonEnd, int pointCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Open-Meteo solar forecast refresh failed.")]
    private partial void LogRefreshFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Open-Meteo solar forecast refresh skipped because no enabled site PV profiles exist and no default AssetId is configured.")]
    private partial void LogSkippedNoProfiles();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping PV profile forecast refresh for Site='{SiteId}', PvSystem='{PvSystemId}', Provider='{Provider}' because it is not handled by the Open-Meteo adapter.")]
    private partial void LogSkippedProvider(string siteId, string pvSystemId, string provider);
}
