using BatteryEms.Domain;

namespace BatteryEms.Api.Contracts;

// HTTP-side response shapes. Kept separate from Domain types so the wire
// contract can evolve without dragging Domain into JSON-serialisation
// concerns. snake_case JSON property names are applied centrally via the
// host's JsonSerializerOptions configuration.

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record HealthResponse(
    string Status,
    DateTimeOffset At,
    IReadOnlyDictionary<string, string>? Components = null);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record BatteryStatusResponse(
    string AssetId,
    TelemetryView? Telemetry,
    DataQualityView? Quality,
    DateTimeOffset? ObservedAt,
    CommandView? LastCommand);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteStatusResponse(
    string AssetId,
    SiteTelemetryView? Telemetry,
    DataQualityView? Quality,
    DateTimeOffset? ObservedAt);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SolarForecastResponse(
    string AssetId,
    string Source,
    string Model,
    DateTimeOffset GeneratedAt,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    double TimeStepSeconds,
    double InstalledDcKw,
    double InstalledAcKw,
    IReadOnlyList<SolarForecastPointView> Points);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SitePvProfilesResponse(
    string SiteId,
    IReadOnlyList<SitePvProfileResponse> Profiles);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteMeasurementsResponse(
    string SiteId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<SiteMeasurementView> Measurements);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteMeasurementView(
    string Source,
    string InstrumentType,
    string InstrumentId,
    string InstrumentName,
    DateTimeOffset Timestamp,
    double? IntervalSeconds,
    string Metric,
    double? Value,
    string Unit,
    string Quality);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteConsumptionResponse(
    string SiteId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<SiteConsumptionView> Readings);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteConsumptionView(
    string Source,
    string PointId,
    string PointName,
    DateTimeOffset Timestamp,
    double? IntervalSeconds,
    double? Apoz,
    double? Aneg,
    double? Ppoz,
    double? Pneg,
    double? ValueMultiplier = null,
    double? ActiveImportKwh = null,
    double? ActiveExportKwh = null,
    double? ReactiveImportKvarh = null,
    double? ReactiveExportKvarh = null);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteBalanceResponse(
    string SiteId,
    DateTimeOffset From,
    DateTimeOffset To,
    string DataQualityStatus,
    double MainGridImportKwh,
    double MainGridExportKwh,
    double SubconsumerConsumptionKwh,
    double OwnConsumptionKwh,
    double DerivedExportFromNegativeConsumptionKwh,
    double GridImportKwh,
    double GridExportKwh,
    double SiteNetBalanceKwh,
    IReadOnlyList<SiteGenerationBalanceView> Generation,
    IReadOnlyList<SiteBalanceWarningView> Warnings,
    IReadOnlyList<SiteBalanceMeterTotalView> MeterTotals);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteGenerationBalanceView(
    string GenerationType,
    double AuxiliaryConsumptionKwh,
    double ExportKwh,
    double NetGenerationKwh);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteBalanceWarningView(
    string Code,
    string Message,
    string? MeterId,
    string? GenerationType);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteBalanceMeterTotalView(
    string MeterId,
    string Name,
    string Role,
    string? GenerationType,
    double ValueMultiplier,
    double? ApozRaw,
    double? AnegRaw,
    double? ApozKwh,
    double? AnegKwh,
    int ReadingCount);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SitePvProfileResponse(
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
    string? Notes)
{
    public static SitePvProfileResponse From(BatteryEms.Application.Site.SitePvProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new(
            profile.SiteId,
            profile.PvSystemId,
            profile.Name,
            profile.ForecastAssetId,
            profile.Enabled,
            profile.Latitude,
            profile.Longitude,
            profile.TiltDegrees,
            profile.AzimuthDegrees,
            profile.InstalledDcKw,
            profile.InverterAcKw,
            profile.TemperatureCoefficientPerDegree,
            profile.SystemLossFraction,
            profile.ForecastHorizonHours,
            profile.ForecastResolutionMinutes,
            profile.ForecastProvider,
            profile.ForecastEngine,
            profile.Notes);
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record AssetsResponse(IReadOnlyList<AssetView> Assets);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record AssetView(
    string AssetId,
    double CapacityKwh,
    double MaxChargePowerKw,
    double MaxDischargePowerKw,
    double MinSocPercent,
    double MaxSocPercent)
{
    public static AssetView From(BatteryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new(
            AssetId: asset.AssetId,
            CapacityKwh: asset.CapacityKwh,
            MaxChargePowerKw: asset.MaxChargePowerKw,
            MaxDischargePowerKw: asset.MaxDischargePowerKw,
            MinSocPercent: asset.MinSocPercent,
            MaxSocPercent: asset.MaxSocPercent);
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record CommandResponse(string AssetId, CommandView? Command);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SchedulesResponse(string AssetId, IReadOnlyList<ScheduleView> Schedules);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record TelemetryView(
    DateTimeOffset Timestamp,
    double SocPercent,
    double SohPercent,
    double ActivePowerKw,
    double ReactivePowerKvar,
    double DcVoltage,
    double DcCurrent,
    double TemperatureCelsius,
    bool Available,
    string FaultStatus)
{
    public static TelemetryView From(BatteryTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        return new(
            Timestamp: telemetry.Timestamp,
            SocPercent: telemetry.SocPercent,
            SohPercent: telemetry.SohPercent,
            ActivePowerKw: telemetry.ActivePowerKw,
            ReactivePowerKvar: telemetry.ReactivePowerKvar,
            DcVoltage: telemetry.DcVoltage,
            DcCurrent: telemetry.DcCurrent,
            TemperatureCelsius: telemetry.TemperatureCelsius,
            Available: telemetry.Available,
            FaultStatus: telemetry.FaultStatus);
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record DataQualityView(string Flag, string Reason)
{
    public static DataQualityView From(DataQuality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return new(quality.Flag.ToString(), quality.Reason);
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record CommandView(
    string CommandId,
    DateTimeOffset Timestamp,
    string AssetId,
    string Mode,
    double ActivePowerKw,
    double? ReactivePowerKvar,
    DateTimeOffset ValidUntil,
    string Reason,
    string Source)
{
    public static CommandView From(BatteryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new(
            CommandId: command.CommandId,
            Timestamp: command.Timestamp,
            AssetId: command.AssetId,
            Mode: command.Mode.ToString(),
            ActivePowerKw: command.ActivePowerKw,
            ReactivePowerKvar: command.ReactivePowerKvar,
            ValidUntil: command.ValidUntil,
            Reason: command.Reason,
            Source: command.Source.ToString());
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record ScheduleView(
    string Type,
    string MarketBidArea,
    int Version,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    IReadOnlyList<ScheduleWindowView> Windows)
{
    public static ScheduleView From(Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return new(
            Type: schedule.Type.ToString(),
            MarketBidArea: schedule.MarketBidArea,
            Version: schedule.Version,
            HorizonStart: schedule.HorizonStart,
            HorizonEnd: schedule.HorizonEnd,
            Windows: schedule.Windows
                .Select(w => new ScheduleWindowView(w.Start, w.End, w.TargetPowerKw))
                .ToArray());
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record ScheduleWindowView(
    DateTimeOffset Start,
    DateTimeOffset End,
    double TargetPowerKw);

// Body for POST /operator/stop. The operator identity is taken from the
// authenticated principal (LH-API-007) rather than the body so a caller
// cannot impersonate another operator just by editing the JSON.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record OperatorStopRequestBody(string AssetId, string Reason);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record OperatorStopResponse(
    string AssetId,
    string Operator,
    string Reason,
    DateTimeOffset ActivatedAt);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record OperatorStopStatusResponse(string AssetId, OperatorStopView? Stop);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record OperatorStopView(
    string Operator,
    string Reason,
    DateTimeOffset ActivatedAt)
{
    public static OperatorStopView From(BatteryEms.Application.Control.OperatorStopState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(
            Operator: state.Operator,
            Reason: state.Reason,
            ActivatedAt: state.ActivatedAt);
    }
}

// Body for POST /markets/day-ahead/optimize. TimeStepSeconds keeps the
// wire shape JSON-friendly (TimeSpan would force ISO-8601 round-trip);
// the endpoint converts it to TimeSpan before handing off to the
// application. PricesPerStep + PriceUnit are optional but, when set,
// must align with the horizon (validated in the application layer).
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record OptimizationRequestBody(
    string AssetId,
    string ScheduleType,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    double TimeStepSeconds,
    IReadOnlyList<double>? PricesPerStep = null,
    string? PriceUnit = null,
    PriceSeriesReferenceBody? PriceSeries = null);

// Body for POST /markets/intraday/reoptimize (RM-M4-01). ResidualStart
// is the moment from which the residual horizon is reoptimised; it
// must align to a window boundary of the existing Intraday schedule
// (D-02). ScheduleType is fixed to "intraday" for this endpoint —
// the use case enforces it; the request body is named distinctly to
// keep wire-shape stable as the day-ahead body evolves separately.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record IntradayReoptimizationRequestBody(
    string AssetId,
    DateTimeOffset ResidualStart,
    DateTimeOffset HorizonEnd,
    double TimeStepSeconds,
    IReadOnlyList<double>? PricesPerStep = null,
    string? PriceUnit = null,
    PriceSeriesReferenceBody? PriceSeries = null);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record PriceSeriesReferenceBody(
    string MarketBidArea,
    string Product,
    string PriceKind,
    string Source);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record PriceSeriesImportRequestBody(
    string MarketBidArea,
    string Product,
    string PriceKind,
    string Unit,
    string Source,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    double TimeStepSeconds,
    IReadOnlyList<double> Values);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record UpsertSitePvProfileRequest(
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
    string? Notes = null);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record PriceSeriesImportResponse(
    string MarketBidArea,
    string Product,
    string PriceKind,
    string Unit,
    string Source,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    double TimeStepSeconds,
    int Count);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record OptimizationResponse(
    Guid RunId,
    OptimizationSolverStatus Status,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    int? ProducedScheduleVersion,
    string TerminationReason,
    ScheduleEconomicsView? Economics = null);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record ScheduleEconomicsView(
    string PriceUnit,
    string Currency,
    double TotalCost,
    double TotalRevenue,
    double TotalLossesKwh,
    double NetProfit,
    IReadOnlyList<ScheduleEconomicsStepView> Steps)
{
    public static ScheduleEconomicsView From(BatteryEms.Application.Optimization.ScheduleEconomicsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new(
            report.PriceUnit,
            report.Currency,
            report.TotalCost,
            report.TotalRevenue,
            report.TotalLossesKwh,
            report.NetProfit,
            report.Steps.Select(ScheduleEconomicsStepView.From).ToArray());
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SiteTelemetryView(
    DateTimeOffset Timestamp,
    double? PvPowerKw,
    double? LoadPowerKw,
    double? GridPowerKw,
    double? IrradianceWPerSquareMeter)
{
    public static SiteTelemetryView From(BatteryEms.Application.Realtime.SiteTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        return new(
            Timestamp: telemetry.Timestamp,
            PvPowerKw: telemetry.PvPowerKw,
            LoadPowerKw: telemetry.LoadPowerKw,
            GridPowerKw: telemetry.GridPowerKw,
            IrradianceWPerSquareMeter: telemetry.IrradianceWPerSquareMeter);
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record SolarForecastPointView(
    DateTimeOffset Timestamp,
    double PowerKw,
    double IrradianceWPerSquareMeter,
    double AmbientTemperatureCelsius,
    double WindSpeedMetersPerSecond,
    int CloudCoverPercent)
{
    public static SolarForecastPointView From(BatteryEms.Application.Forecasting.SolarForecastPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return new(
            point.Timestamp,
            point.PowerKw,
            point.IrradianceWPerSquareMeter,
            point.AmbientTemperatureCelsius,
            point.WindSpeedMetersPerSecond,
            point.CloudCoverPercent);
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record ScheduleEconomicsStepView(
    DateTimeOffset Start,
    DateTimeOffset End,
    double Price,
    double TargetPowerKw,
    double EnergyMwh,
    double BatteryEnergyDeltaKwh,
    double LossesKwh,
    double Cost,
    double Revenue,
    double NetProfit,
    double CumulativeNetProfit)
{
    public static ScheduleEconomicsStepView From(BatteryEms.Application.Optimization.ScheduleEconomicsStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new(
            step.Start,
            step.End,
            step.Price,
            step.TargetPowerKw,
            step.EnergyMwh,
            step.BatteryEnergyDeltaKwh,
            step.LossesKwh,
            step.Cost,
            step.Revenue,
            step.NetProfit,
            step.CumulativeNetProfit);
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record OptimizationRunResponse(
    Guid RunId,
    string AssetId,
    string SolverName,
    OptimizationSolverStatus Status,
    DateTimeOffset HorizonStart,
    DateTimeOffset HorizonEnd,
    double TimeStepSeconds,
    double ObjectiveValue,
    IReadOnlyList<OptimizationObjectiveComponentView> ObjectiveBreakdown,
    IReadOnlyList<string> ConstraintViolations,
    IReadOnlyList<string> Warnings,
    double SolverRuntimeSeconds,
    string TerminationReason,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ScheduleReferenceView> Inputs,
    ScheduleReferenceView? ProducedSchedule)
{
    public static OptimizationRunResponse From(OptimizationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new(
            RunId: run.RunId,
            AssetId: run.AssetId,
            SolverName: run.SolverName,
            Status: run.Status,
            HorizonStart: run.HorizonStart,
            HorizonEnd: run.HorizonEnd,
            TimeStepSeconds: run.TimeStep.TotalSeconds,
            ObjectiveValue: run.ObjectiveValue,
            ObjectiveBreakdown: run.ObjectiveBreakdown.Components
                .Select(c => new OptimizationObjectiveComponentView(c.Name, c.Value, c.Unit))
                .ToArray(),
            ConstraintViolations: run.ConstraintViolations,
            Warnings: run.Warnings,
            SolverRuntimeSeconds: run.SolverRuntime.TotalSeconds,
            TerminationReason: run.TerminationReason,
            CreatedAt: run.CreatedAt,
            Inputs: run.Inputs.Select(ScheduleReferenceView.From).ToArray(),
            ProducedSchedule: run.ProducedSchedule is null
                ? null
                : ScheduleReferenceView.From(run.ProducedSchedule));
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record OptimizationObjectiveComponentView(
    string Name,
    double Value,
    string Unit);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record ScheduleReferenceView(
    string AssetId,
    ScheduleType Type,
    int Version)
{
    public static ScheduleReferenceView From(ScheduleReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new(reference.AssetId, reference.Type, reference.Version);
    }
}

// RM-M4-03-D: GET /health/regelleistung wire shapes.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record RegelleistungHealthResponse(
    DateTimeOffset At,
    string Timebase,
    string DedupeStore,
    string ProductionGate,
    RegelleistungPreconditionsView Preconditions,
    LastActivationView? LastActivation);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record RegelleistungPreconditionsView(
    bool ProductTrust,
    bool TimeSync,
    bool DedupeStoreHealth,
    bool SecurityProfile,
    string ReasonCode);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record LastActivationView(
    string SourceId,
    string ActivationId,
    DateTimeOffset ReceivedAt,
    string ReasonCode,
    bool DispatchRelevant,
    string Details);
