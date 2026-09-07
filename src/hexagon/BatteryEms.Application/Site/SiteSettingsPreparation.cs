using BatteryEms.Application.Realtime;
using BatteryEms.Domain;

namespace BatteryEms.Application.Site;

public sealed record SiteSettingsPreparationCommand(
    string SiteId,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Step)
{
    public SiteSettingsPreparationCommand EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        if (From >= To)
        {
            throw new ArgumentException("Preparation command From must be before To.", nameof(From));
        }

        if (Step <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Step), Step, "Step must be positive.");
        }

        return this;
    }
}

public sealed record SitePreparedSettings(
    string SiteId,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Step,
    IReadOnlyList<PreparedGridConnection> GridConnections,
    IReadOnlyList<SitePreparedStep> Steps,
    IReadOnlyList<SitePreparationWarning> Warnings);

public sealed record PreparedGridConnection(
    string GridConnectionId,
    string Name,
    bool Enabled,
    bool ExportAllowed,
    double EffectiveMaxImportPowerKw,
    double EffectiveMaxExportPowerKw,
    double PlannedVoltageV,
    double MinVoltageV,
    double MaxVoltageV,
    double MaxPhaseImbalancePercent);

public sealed record SitePreparedStep(
    DateTimeOffset IntervalStart,
    DateTimeOffset IntervalEnd,
    double? PvPowerKw,
    double? PvEnergyKwh,
    double? LoadPowerKw,
    double? LoadEnergyKwh,
    double? GridImportPowerKw,
    double? GridImportEnergyKwh,
    double? GridExportPowerKw,
    double? GridExportEnergyKwh,
    IReadOnlyList<GridVoltageHealth> GridVoltageHealth,
    DataQuality DataQuality);

public sealed record GridVoltageHealth(
    string GridConnectionId,
    double? PhaseL1VoltageV,
    double? PhaseL2VoltageV,
    double? PhaseL3VoltageV,
    double? PhaseImbalancePercent,
    bool IsWithinVoltageLimits,
    bool IsWithinPhaseImbalanceLimit,
    bool RequiresIslandTransition);

public sealed record SitePreparationWarning(
    string Code,
    string Message,
    string? SourceId = null);

public interface ISiteSettingsPreparationUseCase
{
    Task<SitePreparedSettings> PrepareAsync(
        SiteSettingsPreparationCommand command,
        CancellationToken cancellationToken);
}

public sealed class DefaultSiteSettingsPreparationUseCase : ISiteSettingsPreparationUseCase
{
    private const string MetricPvPower = "pv_power";
    private const string MetricPvEnergy = "pv_energy";
    private const string MetricInverterYield = "inverter_yield";
    private const string MetricInverterYieldRaw = "inverterYield";
    private const string MetricActivePowerImport = "active_power_import";
    private const string MetricActivePowerExport = "active_power_export";
    private const string MetricActiveEnergyImport = "active_energy_import";
    private const string MetricActiveEnergyExport = "active_energy_export";
    private const string MetricPhaseL1Voltage = "phase_l1_voltage";
    private const string MetricPhaseL2Voltage = "phase_l2_voltage";
    private const string MetricPhaseL3Voltage = "phase_l3_voltage";

    private readonly ISiteRegistry _siteRegistry;
    private readonly ISiteMeasurementStore _measurements;
    private readonly ISiteConsumptionStore _consumption;
    private readonly ISiteTelemetryStore _telemetry;

    public DefaultSiteSettingsPreparationUseCase(
        ISiteRegistry siteRegistry,
        ISiteMeasurementStore measurements,
        ISiteConsumptionStore consumption,
        ISiteTelemetryStore telemetry)
    {
        ArgumentNullException.ThrowIfNull(siteRegistry);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(consumption);
        ArgumentNullException.ThrowIfNull(telemetry);

        _siteRegistry = siteRegistry;
        _measurements = measurements;
        _consumption = consumption;
        _telemetry = telemetry;
    }

    public async Task<SitePreparedSettings> PrepareAsync(
        SiteSettingsPreparationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        command = command.EnsureValid();

        var site = _siteRegistry.Find(command.SiteId)
            ?? throw new InvalidOperationException($"Site '{command.SiteId}' was not found.");
        var warnings = new List<SitePreparationWarning>();
        var rawMeasurements = await _measurements.QueryAsync(
            new SiteMeasurementQuery(command.SiteId, command.From, command.To),
            cancellationToken).ConfigureAwait(false);
        var consumption = await _consumption.QueryAsync(
            new SiteConsumptionQuery(command.SiteId, command.From, command.To),
            cancellationToken).ConfigureAwait(false);

        var preparedGridConnections = site.GridConnections
            .Select(PrepareGridConnection)
            .ToArray();
        var steps = BuildSteps(
            command,
            site,
            rawMeasurements,
            consumption,
            warnings);

        return new SitePreparedSettings(
            site.SiteId,
            command.From,
            command.To,
            command.Step,
            preparedGridConnections,
            steps,
            warnings);
    }

    private SitePreparedStep[] BuildSteps(
        SiteSettingsPreparationCommand command,
        SiteDescriptor site,
        IReadOnlyList<SiteMeasurementReading> measurements,
        IReadOnlyList<SiteConsumptionReading> consumption,
        List<SitePreparationWarning> warnings)
    {
        var result = new List<SitePreparedStep>();
        for (var start = command.From; start < command.To; start = start.Add(command.Step))
        {
            var end = Min(start.Add(command.Step), command.To);
            var bucketMeasurements = measurements
                .Where(reading => IsInBucket(reading.Timestamp, start, end))
                .ToArray();
            var bucketConsumption = consumption
                .Where(reading => IsInBucket(reading.Timestamp, start, end))
                .ToArray();
            var intervalHours = (end - start).TotalHours;

            var pvEnergy = SumEnergy(bucketMeasurements, [MetricPvEnergy, MetricInverterYield, MetricInverterYieldRaw])
                ?? SumTelemetryPvEnergy(site, end, intervalHours, warnings);
            var pvPower = AveragePower(bucketMeasurements, [MetricPvPower])
                ?? EnergyToAveragePower(pvEnergy, intervalHours)
                ?? SumTelemetryPvPower(site, end, warnings);
            pvEnergy ??= AveragePowerToEnergy(pvPower, intervalHours);

            var loadEnergy = SumEnergy(bucketMeasurements, [MetricActiveEnergyImport])
                ?? SumConsumptionEnergy(bucketConsumption, reading => reading.Apoz);
            var loadPower = AveragePower(bucketMeasurements, [MetricActivePowerImport])
                ?? EnergyToAveragePower(loadEnergy, intervalHours);
            loadEnergy ??= AveragePowerToEnergy(loadPower, intervalHours);

            var gridImportEnergy = loadEnergy;
            var gridImportPower = loadPower;
            var gridExportEnergy = SumEnergy(bucketMeasurements, [MetricActiveEnergyExport])
                ?? SumConsumptionEnergy(bucketConsumption, reading => reading.Aneg);
            var gridExportPower = AveragePower(bucketMeasurements, [MetricActivePowerExport])
                ?? EnergyToAveragePower(gridExportEnergy, intervalHours);
            gridExportEnergy ??= AveragePowerToEnergy(gridExportPower, intervalHours);

            var voltageHealth = site.GridConnections
                .Select(connection => BuildVoltageHealth(connection, bucketMeasurements))
                .ToArray();
            var quality = BuildQuality(
                pvPower,
                loadPower,
                voltageHealth);

            result.Add(new SitePreparedStep(
                start,
                end,
                pvPower,
                pvEnergy,
                loadPower,
                loadEnergy,
                gridImportPower,
                gridImportEnergy,
                gridExportPower,
                gridExportEnergy,
                voltageHealth,
                quality));
        }

        return result.ToArray();
    }

    private double? SumTelemetryPvPower(
        SiteDescriptor site,
        DateTimeOffset now,
        List<SitePreparationWarning> warnings)
    {
        double? total = null;
        foreach (var source in site.PvSourceRefs)
        {
            var snapshot = _telemetry.GetLatest(source.SourceId, now);
            if (snapshot is null)
            {
                warnings.Add(new SitePreparationWarning(
                    "pv-telemetry-missing",
                    "PV telemetry snapshot is missing.",
                    source.SourceId));
                continue;
            }

            if (!snapshot.Quality.IsUsableForControl)
            {
                warnings.Add(new SitePreparationWarning(
                    "pv-telemetry-not-usable",
                    $"PV telemetry quality is {snapshot.Quality.Flag}: {snapshot.Quality.Reason}.",
                    source.SourceId));
                continue;
            }

            total = AddNullable(total, snapshot.Telemetry.PvPowerKw);
        }

        return total;
    }

    private double? SumTelemetryPvEnergy(
        SiteDescriptor site,
        DateTimeOffset now,
        double intervalHours,
        List<SitePreparationWarning> warnings) =>
        AveragePowerToEnergy(SumTelemetryPvPower(site, now, warnings), intervalHours);

    private static PreparedGridConnection PrepareGridConnection(GridConnection connection)
    {
        var effectiveExport = connection.Enabled && connection.ExportAllowed
            ? connection.MaxExportPowerKw
            : 0;
        return new PreparedGridConnection(
            connection.GridConnectionId,
            connection.Name,
            connection.Enabled,
            connection.ExportAllowed,
            connection.Enabled ? connection.MaxImportPowerKw : 0,
            effectiveExport,
            connection.PlannedVoltageV,
            connection.MinVoltageV,
            connection.MaxVoltageV,
            connection.MaxPhaseImbalancePercent);
    }

    private static GridVoltageHealth BuildVoltageHealth(
        GridConnection connection,
        IReadOnlyList<SiteMeasurementReading> measurements)
    {
        var l1 = LatestMetric(measurements, connection.MeterSourceRefs, MetricPhaseL1Voltage, "V");
        var l2 = LatestMetric(measurements, connection.MeterSourceRefs, MetricPhaseL2Voltage, "V");
        var l3 = LatestMetric(measurements, connection.MeterSourceRefs, MetricPhaseL3Voltage, "V");
        var withinLimits = IsWithinLimits(l1, connection)
            && IsWithinLimits(l2, connection)
            && IsWithinLimits(l3, connection);
        var imbalance = CalculateImbalancePercent(l1, l2, l3);
        var withinImbalance = imbalance is null || imbalance <= connection.MaxPhaseImbalancePercent;
        var requiresIsland = connection.Enabled && (!withinLimits || !withinImbalance);

        return new GridVoltageHealth(
            connection.GridConnectionId,
            l1,
            l2,
            l3,
            imbalance,
            withinLimits,
            withinImbalance,
            requiresIsland);
    }

    private static DataQuality BuildQuality(
        double? pvPower,
        double? loadPower,
        IReadOnlyList<GridVoltageHealth> voltageHealth)
    {
        if (voltageHealth.Any(health => health.RequiresIslandTransition))
        {
            return DataQuality.ProtocolError("grid-voltage-unsafe");
        }

        if (pvPower is null && loadPower is null)
        {
            return DataQuality.Stale("site-prepared-values-missing");
        }

        return DataQuality.Valid;
    }

    private static double? SumEnergy(
        IReadOnlyList<SiteMeasurementReading> measurements,
        IReadOnlyList<string> metrics)
    {
        var values = measurements
            .Where(reading => metrics.Contains(reading.Metric, StringComparer.Ordinal))
            .Select(ToKwh)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Sum();
    }

    private static double? AveragePower(
        IReadOnlyList<SiteMeasurementReading> measurements,
        IReadOnlyList<string> metrics)
    {
        var values = measurements
            .Where(reading => metrics.Contains(reading.Metric, StringComparer.Ordinal))
            .Select(ToKw)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double? SumConsumptionEnergy(
        IReadOnlyList<SiteConsumptionReading> readings,
        Func<SiteConsumptionReading, double?> selector)
    {
        var values = readings
            .Select(selector)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Sum();
    }

    private static double? LatestMetric(
        IReadOnlyList<SiteMeasurementReading> measurements,
        IReadOnlyList<SiteSourceRef> sourceRefs,
        string metric,
        string unit)
    {
        var sourceIds = sourceRefs
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        return measurements
            .Where(reading => sourceIds.Count == 0 || sourceIds.Contains(reading.InstrumentId))
            .Where(reading => string.Equals(reading.Metric, metric, StringComparison.Ordinal))
            .Where(reading => string.Equals(reading.Unit, unit, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(reading => reading.Timestamp)
            .Select(reading => reading.Value)
            .FirstOrDefault();
    }

    private static double? ToKw(SiteMeasurementReading reading) =>
        reading.Unit switch
        {
            _ when reading.Value is null => null,
            "kW" => reading.Value,
            "W" => reading.Value / 1_000,
            _ => null,
        };

    private static double? ToKwh(SiteMeasurementReading reading) =>
        reading.Unit switch
        {
            _ when reading.Value is null => null,
            "kWh" => reading.Value,
            "Wh" => reading.Value / 1_000,
            _ => null,
        };

    private static double? EnergyToAveragePower(double? energyKwh, double intervalHours) =>
        energyKwh is null ? null : energyKwh / intervalHours;

    private static double? AveragePowerToEnergy(double? powerKw, double intervalHours) =>
        powerKw is null ? null : powerKw * intervalHours;

    private static double? AddNullable(double? left, double? right) =>
        right is null ? left : (left ?? 0) + right.Value;

    private static bool IsWithinLimits(double? voltage, GridConnection connection) =>
        voltage is null || voltage >= connection.MinVoltageV && voltage <= connection.MaxVoltageV;

    private static double? CalculateImbalancePercent(double? l1, double? l2, double? l3)
    {
        if (l1 is null || l2 is null || l3 is null)
        {
            return null;
        }

        var average = (l1.Value + l2.Value + l3.Value) / 3;
        if (average <= 0)
        {
            return null;
        }

        var maxDeviation = Math.Max(
            Math.Abs(l1.Value - average),
            Math.Max(Math.Abs(l2.Value - average), Math.Abs(l3.Value - average)));
        return maxDeviation / average * 100;
    }

    private static bool IsInBucket(DateTimeOffset timestamp, DateTimeOffset start, DateTimeOffset end)
    {
        var utc = timestamp.ToUniversalTime();
        return utc >= start.ToUniversalTime() && utc < end.ToUniversalTime();
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}
