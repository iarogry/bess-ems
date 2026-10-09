using System.Collections.Concurrent;

namespace BatteryEms.Application.Site;

public sealed record SitePvProfile(
    string SiteId,
    string PvSystemId,
    string Name,
    string ForecastAssetId,
    bool Enabled,
    double Latitude,
    double Longitude,
    double TiltDegrees,
    double AzimuthDegrees,
    double InstalledDcKw,
    double InverterAcKw,
    double TemperatureCoefficientPerDegree,
    double SystemLossFraction,
    int ForecastHorizonHours,
    int ForecastResolutionMinutes,
    string ForecastProvider,
    string ForecastEngine,
    string? Notes = null)
{
    public SitePvProfile EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PvSystemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(ForecastAssetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ForecastProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(ForecastEngine);
        if (!double.IsFinite(Latitude) || Latitude is < -90d or > 90d)
        {
            throw new ArgumentOutOfRangeException(nameof(Latitude), Latitude, "Latitude must be between -90 and 90.");
        }
        if (!double.IsFinite(Longitude) || Longitude is < -180d or > 180d)
        {
            throw new ArgumentOutOfRangeException(nameof(Longitude), Longitude, "Longitude must be between -180 and 180.");
        }
        if (!double.IsFinite(TiltDegrees) || TiltDegrees is < 0d or > 90d)
        {
            throw new ArgumentOutOfRangeException(nameof(TiltDegrees), TiltDegrees, "TiltDegrees must be between 0 and 90.");
        }
        if (!double.IsFinite(AzimuthDegrees) || AzimuthDegrees is < -180d or > 180d)
        {
            throw new ArgumentOutOfRangeException(nameof(AzimuthDegrees), AzimuthDegrees, "AzimuthDegrees must be between -180 and 180.");
        }
        if (!double.IsFinite(InstalledDcKw) || InstalledDcKw <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InstalledDcKw), InstalledDcKw, "InstalledDcKw must be positive.");
        }
        if (!double.IsFinite(InverterAcKw) || InverterAcKw <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InverterAcKw), InverterAcKw, "InverterAcKw must be positive.");
        }
        if (InstalledDcKw < InverterAcKw)
        {
            throw new ArgumentOutOfRangeException(nameof(InstalledDcKw), InstalledDcKw, "InstalledDcKw must be greater than or equal to InverterAcKw.");
        }
        if (!double.IsFinite(TemperatureCoefficientPerDegree) || TemperatureCoefficientPerDegree is > 0d or < -0.02d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TemperatureCoefficientPerDegree),
                TemperatureCoefficientPerDegree,
                "TemperatureCoefficientPerDegree must be between -0.02 and 0.");
        }
        if (!double.IsFinite(SystemLossFraction) || SystemLossFraction is < 0d or > 0.5d)
        {
            throw new ArgumentOutOfRangeException(nameof(SystemLossFraction), SystemLossFraction, "SystemLossFraction must be between 0 and 0.5.");
        }
        if (ForecastHorizonHours is < 1 or > 168)
        {
            throw new ArgumentOutOfRangeException(nameof(ForecastHorizonHours), ForecastHorizonHours, "ForecastHorizonHours must be between 1 and 168.");
        }
        if (ForecastResolutionMinutes is not 15 and not 60)
        {
            throw new ArgumentOutOfRangeException(nameof(ForecastResolutionMinutes), ForecastResolutionMinutes, "ForecastResolutionMinutes must be 15 or 60.");
        }

        return this;
    }
}

public interface ISitePvProfileStore
{
    Task UpsertAsync(SitePvProfile profile, CancellationToken cancellationToken);

    Task<SitePvProfile?> FindAsync(string siteId, string pvSystemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SitePvProfile>> ListBySiteAsync(string siteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SitePvProfile>> ListEnabledAsync(CancellationToken cancellationToken);
}

public sealed class InMemorySitePvProfileStore : ISitePvProfileStore
{
    private readonly ConcurrentDictionary<(string SiteId, string PvSystemId), SitePvProfile> _profiles = new();

    public Task UpsertAsync(SitePvProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        profile = profile.EnsureValid();
        _profiles[(profile.SiteId, profile.PvSystemId)] = profile;
        return Task.CompletedTask;
    }

    public Task<SitePvProfile?> FindAsync(string siteId, string pvSystemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pvSystemId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_profiles.TryGetValue((siteId, pvSystemId), out var profile) ? profile : null);
    }

    public Task<IReadOnlyList<SitePvProfile>> ListBySiteAsync(string siteId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SitePvProfile> result = _profiles.Values
            .Where(profile => string.Equals(profile.SiteId, siteId, StringComparison.Ordinal))
            .OrderBy(profile => profile.PvSystemId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SitePvProfile>> ListEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SitePvProfile> result = _profiles.Values
            .Where(profile => profile.Enabled)
            .OrderBy(profile => profile.SiteId, StringComparer.Ordinal)
            .ThenBy(profile => profile.PvSystemId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }
}
