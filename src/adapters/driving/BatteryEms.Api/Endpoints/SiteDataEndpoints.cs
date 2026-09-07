using BatteryEms.Api.Contracts;
using BatteryEms.Application.Site;
using BatteryEms.Application.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class SiteDataEndpoints
{
    private const int DefaultHours = 24;
    private const int DefaultTake = 100;
    private const int MaxHours = 24 * 31;
    private const int MaxTake = 500;

    public static IEndpointRouteBuilder MapSiteData(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        MapMeasurements(routes);
        MapConsumption(routes);
        MapBalance(routes);
        return routes;
    }

    private static void MapMeasurements(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/site/{siteId}/measurements/recent", async (
                string siteId,
                int? hours,
                int? take,
                string? source,
                string? instrumentType,
                string? instrumentId,
                string? metric,
                ISiteMeasurementStore store,
                IClock clock,
                CancellationToken ct) =>
            {
                if (!TryResolveWindow(clock.UtcNow, hours, out var from, out var to, out var error))
                {
                    return Results.BadRequest(new { error });
                }

                var query = new SiteMeasurementQuery(
                    siteId,
                    from,
                    to,
                    EmptyToNull(source),
                    EmptyToNull(instrumentType),
                    EmptyToNull(instrumentId),
                    EmptyToNull(metric));
                var rows = await store.QueryAsync(query, ct).ConfigureAwait(false);
                var limited = rows
                    .OrderByDescending(row => row.Timestamp)
                    .Take(ClampTake(take))
                    .Select(row => new SiteMeasurementView(
                        row.Source,
                        row.InstrumentType,
                        row.InstrumentId,
                        row.InstrumentName,
                        row.Timestamp,
                        row.Interval?.TotalSeconds,
                        row.Metric,
                        row.Value,
                        row.Unit,
                        row.Quality))
                    .ToArray();

                return Results.Ok(new SiteMeasurementsResponse(siteId, from, to, limited));
            })
            .WithName("RecentSiteMeasurements")
            .WithSummary("Recent normalized site measurements for dashboard read models.");
    }

    private static void MapConsumption(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/site/{siteId}/consumption/recent", async (
                string siteId,
                int? hours,
                int? take,
                string? source,
                string? pointId,
                ISiteConsumptionStore store,
                IClock clock,
                CancellationToken ct) =>
            {
                if (!TryResolveWindow(clock.UtcNow, hours, out var from, out var to, out var error))
                {
                    return Results.BadRequest(new { error });
                }

                var query = new SiteConsumptionQuery(
                    siteId,
                    from,
                    to,
                    EmptyToNull(pointId),
                    EmptyToNull(source));
                var rows = await store.QueryAsync(query, ct).ConfigureAwait(false);
                var limited = rows
                    .OrderByDescending(row => row.Timestamp)
                    .Take(ClampTake(take))
                    .Select(row => new SiteConsumptionView(
                        row.Source,
                        row.PointId,
                        row.PointName,
                        row.Timestamp,
                        row.IntervalSeconds,
                        row.Apoz,
                        row.Aneg,
                        row.Ppoz,
                        row.Pneg))
                    .ToArray();

                return Results.Ok(new SiteConsumptionResponse(siteId, from, to, limited));
            })
            .WithName("RecentSiteConsumption")
            .WithSummary("Recent site consumption readings for dashboard read models.");
    }

    private static void MapBalance(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/site/{siteId}/balance", async (
                string siteId,
                DateOnly? date,
                ISiteBalanceUseCase useCase,
                ISiteConsumptionStore consumptionStore,
                IClock clock,
                CancellationToken ct) =>
            {
                if (!TryGetSiteBalanceConfiguration(siteId, out var configuration))
                {
                    return Results.NotFound(new { error = "site-balance-configuration-not-found" });
                }

                var day = date ?? ResolveSiteBalanceLocalDay(siteId, clock.UtcNow);
                var (from, to) = ResolveSiteBalanceWindow(siteId, day);
                var result = await useCase.CalculateAsync(
                    new SiteBalanceCommand(configuration, from, to),
                    ct).ConfigureAwait(false);
                var rawRows = await consumptionStore.QueryAsync(
                    new SiteConsumptionQuery(siteId, from, to, Source: "askue"),
                    ct).ConfigureAwait(false);
                var meterTotals = BuildMeterTotals(configuration, rawRows);

                return Results.Ok(new SiteBalanceResponse(
                    result.SiteId,
                    result.IntervalStart,
                    result.IntervalEnd,
                    result.DataQualityStatus,
                    result.MainGridImportKwh,
                    result.MainGridExportKwh,
                    result.SubconsumerConsumptionKwh,
                    result.OwnConsumptionKwh,
                    result.DerivedExportFromNegativeConsumptionKwh,
                    result.GridImportKwh,
                    result.GridExportKwh,
                    result.SiteNetBalanceKwh,
                    result.Generation.Select(item => new SiteGenerationBalanceView(
                        item.GenerationType,
                        item.AuxiliaryConsumptionKwh,
                        item.ExportKwh,
                        item.NetGenerationKwh)).ToArray(),
                    result.Warnings.Select(warning => new SiteBalanceWarningView(
                        warning.Code,
                        warning.Message,
                        warning.MeterId,
                        warning.GenerationType)).ToArray(),
                    meterTotals));
            })
            .WithName("SiteBalance")
            .WithSummary("Calculated site balance for dashboard read models.");
    }

    private static SiteBalanceMeterTotalView[] BuildMeterTotals(
        SiteBalanceConfiguration configuration,
        IReadOnlyList<SiteConsumptionReading> rows)
    {
        return configuration.Meters
            .Where(meter => meter.Enabled)
            .Select(meter =>
            {
                var meterRows = rows
                    .Where(row => string.Equals(row.PointId, meter.MeterId, StringComparison.Ordinal))
                    .ToArray();
                var apozRaw = SumNullable(meterRows.Select(row => row.Apoz));
                var anegRaw = SumNullable(meterRows.Select(row => row.Aneg));
                return new SiteBalanceMeterTotalView(
                    meter.MeterId,
                    meter.Name,
                    ToWireRole(meter.Role),
                    meter.GenerationType,
                    meter.ValueMultiplier,
                    apozRaw,
                    anegRaw,
                    SumEnergy(meterRows, row => row.Apoz, meter.ValueMultiplier),
                    SumEnergy(meterRows, row => row.Aneg, meter.ValueMultiplier),
                    meterRows.Length);
            })
            .OrderBy(item => item.Role, StringComparer.Ordinal)
            .ThenBy(item => item.MeterId, StringComparer.Ordinal)
            .ToArray();
    }

    private static double? SumEnergy(
        IReadOnlyList<SiteConsumptionReading> rows,
        Func<SiteConsumptionReading, double?> selector,
        double multiplier)
    {
        double total = 0;
        var hasValue = false;
        foreach (var row in rows)
        {
            var value = selector(row);
            if (value is null)
            {
                continue;
            }

            total += value.Value * multiplier * (row.IntervalSeconds ?? 3600) / 3600.0;
            hasValue = true;
        }

        return hasValue ? total : null;
    }

    private static double? SumNullable(IEnumerable<double?> values)
    {
        double total = 0;
        var hasValue = false;
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            total += value.Value;
            hasValue = true;
        }

        return hasValue ? total : null;
    }

    private static bool TryGetSiteBalanceConfiguration(
        string siteId,
        out SiteBalanceConfiguration configuration)
    {
        configuration = Khlibzavod5SiteBalanceConfiguration;
        return string.Equals(siteId, configuration.SiteId, StringComparison.Ordinal);
    }

    private static DateOnly ResolveSiteBalanceLocalDay(string siteId, DateTimeOffset utcNow)
    {
        if (string.Equals(siteId, Khlibzavod5SiteBalanceConfiguration.SiteId, StringComparison.Ordinal))
        {
            var local = utcNow + ResolveKyivOffset(utcNow.UtcDateTime);
            return DateOnly.FromDateTime(local.DateTime);
        }

        return DateOnly.FromDateTime(utcNow.UtcDateTime);
    }

    private static (DateTimeOffset From, DateTimeOffset To) ResolveSiteBalanceWindow(string siteId, DateOnly day)
    {
        if (string.Equals(siteId, Khlibzavod5SiteBalanceConfiguration.SiteId, StringComparison.Ordinal))
        {
            return ResolveKyivDayWindow(day);
        }

        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return (from, from.AddDays(1));
    }

    private static (DateTimeOffset From, DateTimeOffset To) ResolveKyivDayWindow(DateOnly day)
    {
        var localStart = day.ToDateTime(TimeOnly.MinValue);
        var localEnd = localStart.AddDays(1);
        var startOffset = ResolveKyivOffset(localStart);
        var endOffset = ResolveKyivOffset(localEnd);
        var from = new DateTimeOffset(localStart, startOffset).ToUniversalTime();
        var to = new DateTimeOffset(localEnd, endOffset).ToUniversalTime();
        return (from, to);
    }

    private static TimeSpan ResolveKyivOffset(DateTime localDateTime)
    {
        var year = localDateTime.Year;
        var dstStartLocal = LastSunday(year, 3).AddHours(3);
        var dstEndLocal = LastSunday(year, 10).AddHours(4);
        var local = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return local >= dstStartLocal && local < dstEndLocal
            ? TimeSpan.FromHours(3)
            : TimeSpan.FromHours(2);
    }

    private static DateTime LastSunday(int year, int month)
    {
        var date = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (date.DayOfWeek != DayOfWeek.Sunday)
        {
            date = date.AddDays(-1);
        }

        return date;
    }

    private static string ToWireRole(SiteBalanceMeterRole role) => role switch
    {
        SiteBalanceMeterRole.MainGridMeter => "main_grid_meter",
        SiteBalanceMeterRole.SubconsumerMeter => "subconsumer_meter",
        SiteBalanceMeterRole.GenerationMeter => "generation_meter",
        SiteBalanceMeterRole.TechnicalMeter => "technical_meter",
        SiteBalanceMeterRole.CheckMeter => "check_meter",
        _ => "disabled",
    };

    private static readonly SiteBalanceConfiguration Khlibzavod5SiteBalanceConfiguration = new(
        "site-khlibzavod-5",
        [
            new("869", "OGK Sputnik 1", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true, ValueMultiplier: 400),
            new("870", "PS Sputnik f.11", SiteBalanceMeterRole.MainGridMeter, Enabled: true, ValueMultiplier: 400),
            new("871", "PS Sputnik f.2", SiteBalanceMeterRole.MainGridMeter, Enabled: true, ValueMultiplier: 400),
            new("899", "SPD-FO Savenkov I.I.", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true, ValueMultiplier: 400),
            new("900", "GK Khlibozavodskyi", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true, ValueMultiplier: 400),
            new("929", "KGU", SiteBalanceMeterRole.GenerationMeter, Enabled: true, GenerationType: "gas_cogeneration", ValueMultiplier: 400),
            new("872", "PS Khlibzavod 5 cell 11", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true, ValueMultiplier: 400),
            new("873", "PS Khlibzavod 5 cell 13", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true, ValueMultiplier: 400),
            new("874", "PS Khlibzavod 5 cell 14", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true, ValueMultiplier: 400),
            new("875", "PS Khlibzavod 5 cell 18", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true, ValueMultiplier: 400),
            new("876", "TOV Terra-Forum", SiteBalanceMeterRole.SubconsumerMeter, Enabled: true, ValueMultiplier: 400),
        ],
        CrossCheckTolerancePercent: 5);

    private static bool TryResolveWindow(
        DateTimeOffset now,
        int? hours,
        out DateTimeOffset from,
        out DateTimeOffset to,
        out string? error)
    {
        var resolvedHours = hours ?? DefaultHours;
        if (resolvedHours <= 0 || resolvedHours > MaxHours)
        {
            from = default;
            to = default;
            error = "hours-out-of-range";
            return false;
        }

        to = now;
        from = now - TimeSpan.FromHours(resolvedHours);
        error = null;
        return true;
    }

    private static int ClampTake(int? take)
    {
        var resolvedTake = take ?? DefaultTake;
        return Math.Clamp(resolvedTake, 1, MaxTake);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
