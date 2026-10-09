namespace BatteryEms.Application.Site;

public sealed record SiteBalanceConfiguration(
    string SiteId,
    IReadOnlyList<SiteBalanceMeter> Meters,
    double CrossCheckTolerancePercent = 5)
{
    public SiteBalanceConfiguration EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        if (CrossCheckTolerancePercent < 0 || !double.IsFinite(CrossCheckTolerancePercent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CrossCheckTolerancePercent),
                CrossCheckTolerancePercent,
                "Cross-check tolerance must be a finite non-negative percentage.");
        }

        var meterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meter in Meters)
        {
            meter.EnsureValid();
            if (!meterIds.Add(meter.MeterId))
            {
                throw new ArgumentException($"Duplicate site balance meter_id '{meter.MeterId}'.", nameof(Meters));
            }
        }

        if (!Meters.Any(meter => meter.Enabled && meter.Role == SiteBalanceMeterRole.MainGridMeter))
        {
            throw new ArgumentException("At least one enabled main grid meter is required.", nameof(Meters));
        }

        return this;
    }
}

public sealed record SiteBalanceMeter(
    string MeterId,
    string Name,
    SiteBalanceMeterRole Role,
    bool Enabled,
    string? GenerationType = null,
    double ValueMultiplier = 1)
{
    public SiteBalanceMeter EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(MeterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (ValueMultiplier <= 0 || !double.IsFinite(ValueMultiplier))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ValueMultiplier),
                ValueMultiplier,
                "Site balance meter value multiplier must be finite and greater than zero.");
        }

        if (Role == SiteBalanceMeterRole.GenerationMeter)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(GenerationType);
        }

        return this;
    }
}

public enum SiteBalanceMeterRole
{
    MainGridMeter,
    SubconsumerMeter,
    GenerationMeter,
    TechnicalMeter,
    CheckMeter,
    Disabled,
}

public sealed record SiteBalanceCalculationRequest(
    SiteBalanceConfiguration Configuration,
    DateTimeOffset IntervalStart,
    DateTimeOffset IntervalEnd,
    IReadOnlyList<SiteMeasurementReading> AskueReadings,
    IReadOnlyList<SiteMeasurementReading> CrossCheckReadings)
{
    public SiteBalanceCalculationRequest EnsureValid()
    {
        ArgumentNullException.ThrowIfNull(Configuration);
        Configuration.EnsureValid();
        if (IntervalStart >= IntervalEnd)
        {
            throw new ArgumentException("IntervalStart must be before IntervalEnd.", nameof(IntervalStart));
        }

        return this;
    }
}

public sealed record SiteBalanceResult(
    string SiteId,
    DateTimeOffset IntervalStart,
    DateTimeOffset IntervalEnd,
    double MainGridImportKwh,
    double MainGridExportKwh,
    double SubconsumerConsumptionKwh,
    double OwnConsumptionKwh,
    double GridImportKwh,
    double GridExportKwh,
    double DerivedExportFromNegativeConsumptionKwh,
    double SiteNetBalanceKwh,
    IReadOnlyList<GenerationBalance> Generation,
    IReadOnlyList<SiteBalanceWarning> Warnings,
    string DataQualityStatus);

public sealed record GenerationBalance(
    string GenerationType,
    double AuxiliaryConsumptionKwh,
    double ExportKwh,
    double NetGenerationKwh);

public sealed record SiteBalanceWarning(
    string Code,
    string Message,
    string? MeterId = null,
    string? GenerationType = null);

public static class SiteBalanceCalculator
{
    private const string ActiveEnergyImport = "active_energy_import";
    private const string ActiveEnergyExport = "active_energy_export";
    private const string PvEnergy = "pv_energy";
    private const string InverterYield = "inverter_yield";

    public static SiteBalanceResult Calculate(SiteBalanceCalculationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();

        var warnings = new List<SiteBalanceWarning>();
        var enabledMeters = request.Configuration.Meters
            .Where(meter => meter.Enabled)
            .ToArray();

        foreach (var meter in enabledMeters)
        {
            if (!HasAnyAskueReading(request.AskueReadings, meter.MeterId))
            {
                warnings.Add(new SiteBalanceWarning(
                    "meter-reading-missing",
                    "ASKUE meter has no usable reading in the interval.",
                    meter.MeterId));
            }
        }

        foreach (var meter in enabledMeters)
        {
            var intervals = request.AskueReadings.Where(row => row.InstrumentId == meter.MeterId && row.Interval is not null)
                .GroupBy(row => row.Timestamp).Select(group => group.First().Interval!.Value.TotalSeconds).ToArray();
            if (intervals.Length > 0 && intervals.Sum() < (request.IntervalEnd - request.IntervalStart).TotalSeconds)
            {
                warnings.Add(new SiteBalanceWarning("meter-window-incomplete",
                    "Meter energy covers only part of the requested day; totals are partial.", meter.MeterId));
            }
        }

        var mainMeters = enabledMeters
            .Where(meter => meter.Role == SiteBalanceMeterRole.MainGridMeter)
            .ToArray();
        var subconsumerMeters = enabledMeters
            .Where(meter => meter.Role == SiteBalanceMeterRole.SubconsumerMeter)
            .ToArray();
        var generationMeters = enabledMeters
            .Where(meter => meter.Role == SiteBalanceMeterRole.GenerationMeter)
            .ToArray();

        var mainImport = SumEnergy(request.AskueReadings, mainMeters, ActiveEnergyImport);
        var mainExport = SumEnergy(request.AskueReadings, mainMeters, ActiveEnergyExport);
        var subconsumerConsumption = SumEnergy(request.AskueReadings, subconsumerMeters, ActiveEnergyImport);
        var generation = BuildGenerationBalances(request.AskueReadings, generationMeters);
        var auxiliaryConsumption = generation.Sum(item => item.AuxiliaryConsumptionKwh);

        var calculatedOwnConsumption = mainImport - subconsumerConsumption + auxiliaryConsumption;
        var ownConsumption = Math.Max(0, calculatedOwnConsumption);
        // A negative residual is not evidence of a metered export.
        if (calculatedOwnConsumption < 0)
        {
            warnings.Add(new SiteBalanceWarning("balance-negative-residual",
                "Subconsumer energy exceeds the available site balance; check coverage and meter roles."));
        }
        var derivedExport = 0.0;
        var gridImport = mainImport;
        var gridExport = mainExport;
        var siteNetBalance = gridImport - gridExport;

        AddCrossCheckWarnings(
            request,
            generation,
            warnings);

        var quality = warnings.Any(warning => warning.Code is "meter-reading-missing" or "meter-window-incomplete" or "balance-negative-residual")
            ? "incomplete"
            : "complete";

        return new SiteBalanceResult(
            request.Configuration.SiteId,
            request.IntervalStart,
            request.IntervalEnd,
            mainImport,
            mainExport,
            subconsumerConsumption,
            ownConsumption,
            gridImport,
            gridExport,
            derivedExport,
            siteNetBalance,
            generation,
            warnings,
            quality);
    }

    private static GenerationBalance[] BuildGenerationBalances(
        IReadOnlyList<SiteMeasurementReading> readings,
        IReadOnlyList<SiteBalanceMeter> generationMeters)
    {
        return generationMeters
            .GroupBy(meter => meter.GenerationType!, StringComparer.Ordinal)
            .Select(group =>
            {
                var meters = group.ToArray();
                var auxiliary = SumEnergy(readings, meters, ActiveEnergyImport);
                var export = SumEnergy(readings, meters, ActiveEnergyExport);
                return new GenerationBalance(
                    group.Key,
                    auxiliary,
                    export,
                    export - auxiliary);
            })
            .OrderBy(item => item.GenerationType, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddCrossCheckWarnings(
        SiteBalanceCalculationRequest request,
        IReadOnlyList<GenerationBalance> generation,
        List<SiteBalanceWarning> warnings)
    {
        var askueSolar = generation
            .Where(item => string.Equals(item.GenerationType, "solar", StringComparison.Ordinal))
            .Sum(item => Math.Max(0, item.NetGenerationKwh));
        var apiSolar = request.CrossCheckReadings
            .Where(reading => IsEnergyMetric(reading.Metric))
            .Select(ToKwh)
            .Where(value => value is not null)
            .Sum(value => value!.Value);

        if (askueSolar <= 0 || apiSolar <= 0)
        {
            return;
        }

        var deltaPercent = Math.Abs(apiSolar - askueSolar) / askueSolar * 100;
        if (deltaPercent > request.Configuration.CrossCheckTolerancePercent)
        {
            warnings.Add(new SiteBalanceWarning(
                "generation-crosscheck-mismatch",
                $"Solar generation differs from cross-check source by {deltaPercent:F2}%.",
                GenerationType: "solar"));
        }
    }

    private static bool HasAnyAskueReading(
        IReadOnlyList<SiteMeasurementReading> readings,
        string meterId) =>
        readings.Any(reading =>
            string.Equals(reading.InstrumentId, meterId, StringComparison.Ordinal)
            && string.Equals(reading.Source, "askue", StringComparison.Ordinal)
            && reading.Value is not null
            && IsEnergyMetric(reading.Metric));

    private static double SumEnergy(
        IReadOnlyList<SiteMeasurementReading> readings,
        IReadOnlyList<SiteBalanceMeter> meters,
        string metric)
    {
        var meterIds = meters
            .Select(meter => meter.MeterId)
            .ToHashSet(StringComparer.Ordinal);
        return readings
            .Where(reading => meterIds.Contains(reading.InstrumentId))
            .Where(reading => string.Equals(reading.Source, "askue", StringComparison.Ordinal))
            .Where(reading => string.Equals(reading.Metric, metric, StringComparison.Ordinal))
            .Select(ToKwh)
            .Where(value => value is not null)
            .Sum(value => value!.Value);
    }

    private static bool IsEnergyMetric(string metric) =>
        string.Equals(metric, ActiveEnergyImport, StringComparison.Ordinal)
        || string.Equals(metric, ActiveEnergyExport, StringComparison.Ordinal)
        || string.Equals(metric, PvEnergy, StringComparison.Ordinal)
        || string.Equals(metric, InverterYield, StringComparison.Ordinal);

    private static double? ToKwh(SiteMeasurementReading reading) =>
        reading.Unit switch
        {
            _ when reading.Value is null => null,
            "kWh" => reading.Value,
            "Wh" => reading.Value / 1_000,
            _ => null,
        };
}

public sealed record SiteBalanceCommand(
    SiteBalanceConfiguration Configuration,
    DateTimeOffset From,
    DateTimeOffset To)
{
    public SiteBalanceCommand EnsureValid()
    {
        ArgumentNullException.ThrowIfNull(Configuration);
        Configuration.EnsureValid();
        if (From >= To)
        {
            throw new ArgumentException("Site balance command From must be before To.", nameof(From));
        }

        return this;
    }
}

public interface ISiteBalanceUseCase
{
    Task<SiteBalanceResult> CalculateAsync(
        SiteBalanceCommand command,
        CancellationToken cancellationToken);
}

public sealed class DefaultSiteBalanceUseCase : ISiteBalanceUseCase
{
    private const string AskueSource = "askue";
    private const string ActiveEnergyImport = "active_energy_import";
    private const string ActiveEnergyExport = "active_energy_export";

    private readonly ISiteConsumptionStore _consumption;

    public DefaultSiteBalanceUseCase(ISiteConsumptionStore consumption)
    {
        _consumption = consumption ?? throw new ArgumentNullException(nameof(consumption));
    }

    public async Task<SiteBalanceResult> CalculateAsync(
        SiteBalanceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        command = command.EnsureValid();
        var consumption = await _consumption.QueryAsync(
            new SiteConsumptionQuery(
                command.Configuration.SiteId,
                command.From,
                command.To,
                Source: AskueSource),
            cancellationToken).ConfigureAwait(false);

        var multipliers = command.Configuration.Meters
            .ToDictionary(meter => meter.MeterId, meter => meter.ValueMultiplier, StringComparer.Ordinal);
        var readings = new List<SiteMeasurementReading>(consumption.Count * 2);
        foreach (var item in consumption)
        {
            var multiplier = multipliers.GetValueOrDefault(item.PointId, 1);
            AddReading(readings, item, ActiveEnergyImport, item.Apoz, multiplier);
            AddReading(readings, item, ActiveEnergyExport, item.Aneg, multiplier);
        }

        return SiteBalanceCalculator.Calculate(new SiteBalanceCalculationRequest(
            command.Configuration,
            command.From,
            command.To,
            readings,
            []));
    }

    private static void AddReading(
        List<SiteMeasurementReading> readings,
        SiteConsumptionReading source,
        string metric,
        double? value,
        double multiplier)
    {
        if (value is null)
        {
            return;
        }

        readings.Add(new SiteMeasurementReading(
            source.SiteId,
            AskueSource,
            "meter",
            source.PointId,
            source.PointName,
            source.Timestamp,
            source.IntervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            metric,
            ToKwh(value.Value, multiplier, source.IntervalSeconds),
            "kWh",
            "measured"));
    }

    private static double ToKwh(double sourceValue, double multiplier, int? intervalSeconds)
    {
        // ASKUE profile values already contain energy for the reported interval.
        return sourceValue * multiplier;
    }
}
