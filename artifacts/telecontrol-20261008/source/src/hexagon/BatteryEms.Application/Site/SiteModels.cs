namespace BatteryEms.Application.Site;

public sealed record SiteDescriptor(
    string SiteId,
    string Name,
    string TimeZone,
    string MarketBidArea,
    IReadOnlyList<GridConnection> GridConnections,
    IReadOnlyList<string> AssetIds,
    IReadOnlyList<SiteSourceRef> PvSourceRefs,
    IReadOnlyList<SiteSourceRef> LoadSourceRefs,
    IReadOnlyList<SiteSourceRef> GridMeterSourceRefs,
    SiteInstrumentSet Instruments);

public sealed record GridConnection(
    string GridConnectionId,
    string Name,
    double PlannedVoltageV,
    double MinVoltageV,
    double MaxVoltageV,
    double MaxPhaseImbalancePercent,
    double MaxImportPowerKw,
    double MaxExportPowerKw,
    bool ExportAllowed,
    bool Enabled,
    IReadOnlyList<SiteSourceRef> MeterSourceRefs,
    string? Notes = null);

public sealed record SiteSourceRef(
    string SourceId,
    string SourceType,
    bool IsControllable = false);

public sealed record SiteInstrumentSet(IReadOnlyList<SiteInstrument> Values)
{
    public bool Contains(SiteInstrument instrument) => Values.Contains(instrument);
}

public enum SiteInstrument
{
    BatteryArbitrage,
    BatterySelfConsumption,
    PvSelfConsumption,
    PvCurtailment,
    GridImportLimit,
    GridExportLimit,
    LoadShift,
    DispatchableGenerator,
    MarketExport,
}
