using BatteryEms.Application.Api;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Time;
using BatteryEms.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace BatteryEms.Api.Endpoints;

public sealed class FleetSiteDefinition
{
    public string SiteId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<FleetSourceDefinition> Sources { get; set; } = [];
}

public sealed class FleetSourceDefinition
{
    public string PhysicalId { get; set; } = string.Empty;
    public string TelemetryId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
}

public sealed record FleetSourcePower(string PhysicalId, string TelemetryId, string Kind,
    double? PowerKw, string Quality, string Reason, DateTimeOffset? ObservedAt);
public sealed record FleetPower(double? PowerKw, int AvailableSources, int ExpectedSources, string Status);
public sealed record FleetSitePower(string SiteId, string Name, FleetPower Generation, FleetPower Consumption,
    IReadOnlyList<FleetSourcePower> Sources);
public sealed record FleetOverviewResponse(DateTimeOffset At, bool MembershipConfirmed, FleetPower Generation, FleetPower Consumption,
    IReadOnlyList<FleetSitePower> Sites);

public static class FleetOverview
{
    public static async Task<IResult> ReadAsync(IConfiguration configuration, ISiteTelemetryStore siteTelemetry,
        IBatteryStatusQuery batteryTelemetry, IClock clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(siteTelemetry);
        ArgumentNullException.ThrowIfNull(batteryTelemetry);
        ArgumentNullException.ThrowIfNull(clock);
        var sites = configuration.GetSection("Dashboard:Sites").Get<FleetSiteDefinition[]>() ?? [];
        if (!Validate(sites))
        {
            return Results.Problem("Dashboard site/source bindings are invalid or duplicate a physical component. Configure one primary source per component.", statusCode: 503);
        }
        var now = clock.UtcNow;
        var rows = new List<FleetSitePower>();
        foreach (var site in sites)
        {
            var sources = new List<FleetSourcePower>();
            foreach (var source in site.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sources.Add(await ReadSourceAsync(source, siteTelemetry, batteryTelemetry, now, cancellationToken).ConfigureAwait(false));
            }
            rows.Add(new FleetSitePower(site.SiteId, site.Name,
                Sum(sources.Where(source => source.Kind != "load")),
                Sum(sources.Where(source => source.Kind == "load")), sources));
        }
        var confirmed = configuration.GetValue<bool>("Dashboard:MembershipConfirmed");
        var generation = SumSites(rows.Select(row => row.Generation));
        var consumption = SumSites(rows.Select(row => row.Consumption));
        return Results.Ok(new FleetOverviewResponse(now, confirmed,
            confirmed ? generation : generation with { PowerKw = null, Status = "membership_pending" },
            confirmed ? consumption : consumption with { PowerKw = null, Status = "membership_pending" }, rows));
    }

    private static bool Validate(IReadOnlyList<FleetSiteDefinition> sites)
    {
        var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var siteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            if (string.IsNullOrWhiteSpace(site.SiteId) || !siteIds.Add(site.SiteId))
            {
                return false;
            }
            foreach (var source in site.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.PhysicalId) || string.IsNullOrWhiteSpace(source.TelemetryId)
                    || source.Kind is not ("pv" or "chp" or "battery" or "load")
                    || !physical.Add((source.Kind == "load" ? "load:" : "generation:") + source.PhysicalId)
                    || !bindings.Add(source.Kind + ":" + source.TelemetryId))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static async Task<FleetSourcePower> ReadSourceAsync(FleetSourceDefinition source,
        ISiteTelemetryStore siteTelemetry, IBatteryStatusQuery batteryTelemetry,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        double? power;
        DataQuality? quality;
        DateTimeOffset? observed;
        DateTimeOffset? measured;
        if (source.Kind == "battery")
        {
            var view = await batteryTelemetry.FindAsync(source.TelemetryId, now, cancellationToken).ConfigureAwait(false);
            power = view?.Telemetry?.ActivePowerKw;
            quality = view?.Quality;
            observed = view?.ObservedAt;
            measured = view?.Telemetry?.Timestamp;
        }
        else
        {
            var snapshot = siteTelemetry.GetLatest(source.TelemetryId, now);
            power = source.Kind == "load" ? snapshot?.Telemetry.LoadPowerKw : snapshot?.Telemetry.PvPowerKw;
            quality = snapshot?.Quality;
            observed = snapshot?.ReceivedAt;
            measured = snapshot?.Telemetry.Timestamp;
        }
        return SourcePower(source, power, quality, observed, measured, now);
    }

    private static FleetSourcePower SourcePower(FleetSourceDefinition source, double? power,
        DataQuality? quality, DateTimeOffset? observed, DateTimeOffset? measured, DateTimeOffset now)
    {
        var fresh = observed is not null && measured is not null
            && now >= observed && now - observed <= TimeSpan.FromMinutes(10)
            && now >= measured && now - measured <= TimeSpan.FromMinutes(10);
        var usable = fresh && quality?.Flag is DataQualityState.Valid or DataQualityState.Substituted
            && power is double value && double.IsFinite(value)
            && (source.Kind == "battery" || value >= 0);
        return new FleetSourcePower(source.PhysicalId, source.TelemetryId, source.Kind,
            usable ? source.Kind == "battery" ? Math.Max(power!.Value, 0) : power : null,
            usable ? quality!.Flag.ToString() : "Unavailable",
            usable ? quality!.Reason : !fresh ? "missing-or-stale" : "missing-or-invalid-power",
            observed);
    }

    private static FleetPower SumSites(IEnumerable<FleetPower> powers)
    {
        var items = powers.ToArray();
        var available = items.Where(item => item.PowerKw is not null).ToArray();
        var sum = available.Length == 0 ? (double?)null : available.Sum(item => item.PowerKw!.Value);
        var status = sum is null ? "unavailable" : items.Any(item => item.Status is "unavailable" or "partial")
            ? "partial" : items.Any(item => item.Status == "estimated") ? "estimated" : "complete";
        return new FleetPower(sum is double number && !double.IsFinite(number) ? null : sum,
            items.Sum(item => item.AvailableSources), items.Sum(item => item.ExpectedSources), status);
    }

    private static FleetPower Sum(IEnumerable<FleetSourcePower> sources)
    {
        var items = sources.ToArray();
        var available = items.Where(source => source.PowerKw is not null).ToArray();
        var sum = available.Length == 0 ? (double?)null : available.Sum(source => source.PowerKw!.Value);
        if (sum is double number && !double.IsFinite(number)) { return new FleetPower(null, 0, items.Length, "unavailable"); }
        var status = available.Length == 0 ? "unavailable"
            : available.Length < items.Length ? "partial"
            : available.Any(source => source.Quality == "Substituted") ? "estimated" : "complete";
        return new FleetPower(sum, available.Length, Math.Max(1, items.Length), status);
    }
}
