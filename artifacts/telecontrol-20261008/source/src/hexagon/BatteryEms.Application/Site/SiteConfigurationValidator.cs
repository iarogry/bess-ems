namespace BatteryEms.Application.Site;

public sealed class SiteConfigurationValidator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept instance-based so the validator can be registered in DI and receive policy options in later site-optimization slices.")]
    public SiteConfigurationValidationResult Validate(SiteDescriptor site)
    {
        ArgumentNullException.ThrowIfNull(site);

        var errors = new List<SiteConfigurationValidationError>();
        ValidateRequiredText(site.SiteId, "site_id", "site-id-required", errors);
        ValidateRequiredText(site.TimeZone, "timezone", "timezone-required", errors);
        ValidateRequiredText(site.MarketBidArea, "market_bid_area", "market-bid-area-required", errors);
        ValidateGridConnections(site.GridConnections, errors);
        ValidateInstruments(site, errors);

        return errors.Count == 0
            ? SiteConfigurationValidationResult.Valid
            : SiteConfigurationValidationResult.Invalid(errors);
    }

    private static void ValidateGridConnections(
        IReadOnlyList<GridConnection> gridConnections,
        List<SiteConfigurationValidationError> errors)
    {
        if (gridConnections.Count == 0)
        {
            errors.Add(new("grid-connections-required", "grid_connections", "Site must define at least one grid connection."));
            return;
        }

        if (!gridConnections.Any(connection => connection.Enabled))
        {
            errors.Add(new("grid-connection-enabled-required", "grid_connections", "At least one grid connection must be enabled."));
        }

        foreach (var connection in gridConnections)
        {
            ValidateGridConnection(connection, errors);
        }
    }

    private static void ValidateGridConnection(
        GridConnection connection,
        List<SiteConfigurationValidationError> errors)
    {
        ValidateRequiredText(connection.GridConnectionId, "grid_connection_id", "grid-connection-id-required", errors);
        ValidatePositive(connection.PlannedVoltageV, "planned_voltage_v", "planned-voltage-invalid", errors);
        if (!double.IsFinite(connection.MinVoltageV) || connection.MinVoltageV >= connection.PlannedVoltageV)
        {
            errors.Add(new("min-voltage-invalid", "min_voltage_v", "Min voltage must be finite and below planned voltage."));
        }
        if (!double.IsFinite(connection.MaxVoltageV) || connection.MaxVoltageV <= connection.PlannedVoltageV)
        {
            errors.Add(new("max-voltage-invalid", "max_voltage_v", "Max voltage must be finite and above planned voltage."));
        }
        if (!double.IsFinite(connection.MaxPhaseImbalancePercent) || connection.MaxPhaseImbalancePercent < 0)
        {
            errors.Add(new("phase-imbalance-limit-invalid", "max_phase_imbalance_percent", "Phase imbalance limit must be finite and non-negative."));
        }
        ValidatePositive(connection.MaxImportPowerKw, "max_import_power_kw", "max-import-power-invalid", errors);
        if (!double.IsFinite(connection.MaxExportPowerKw) || connection.MaxExportPowerKw < 0)
        {
            errors.Add(new("max-export-power-invalid", "max_export_power_kw", "Max export power must be finite and non-negative."));
        }
        if (connection.ExportAllowed && connection.MaxExportPowerKw <= 0)
        {
            errors.Add(new("export-limit-required", "max_export_power_kw", "Export-enabled grid connection must define positive export power."));
        }
    }

    private static void ValidateInstruments(
        SiteDescriptor site,
        List<SiteConfigurationValidationError> errors)
    {
        foreach (var instrument in site.Instruments.Values.Distinct())
        {
            ValidateInstrument(site, instrument, errors);
        }
    }

    private static void ValidateInstrument(
        SiteDescriptor site,
        SiteInstrument instrument,
        List<SiteConfigurationValidationError> errors)
    {
        switch (instrument)
        {
            case SiteInstrument.BatteryArbitrage:
                RequireAny(site.AssetIds, "battery-arbitrage-requires-asset", errors);
                break;
            case SiteInstrument.BatterySelfConsumption:
                RequireAny(site.AssetIds, "battery-self-consumption-requires-asset", errors);
                RequireAny(site.PvSourceRefs, "battery-self-consumption-requires-pv", errors);
                break;
            case SiteInstrument.PvSelfConsumption:
                RequireAny(site.PvSourceRefs, "pv-self-consumption-requires-pv", errors);
                break;
            case SiteInstrument.PvCurtailment:
                RequireControllable(site.PvSourceRefs, "pv-curtailment-requires-control", errors);
                break;
            case SiteInstrument.GridImportLimit:
                RequireAny(site.GridConnections, "grid-import-limit-requires-grid", errors);
                break;
            case SiteInstrument.GridExportLimit:
                if (!site.GridConnections.Any(connection => connection.Enabled && connection.ExportAllowed && connection.MaxExportPowerKw > 0))
                {
                    errors.Add(new("grid-export-limit-requires-export", "grid_connections", "Grid export limit requires an enabled export-capable grid connection."));
                }
                break;
            case SiteInstrument.LoadShift:
                RequireControllable(site.LoadSourceRefs, "load-shift-requires-flexible-load", errors);
                break;
            case SiteInstrument.DispatchableGenerator:
                errors.Add(new("dispatchable-generator-not-configured", "available_instruments", "Dispatchable generator requires generator configuration."));
                break;
            case SiteInstrument.MarketExport:
                if (!site.GridConnections.Any(connection => connection.Enabled && connection.ExportAllowed && connection.MaxExportPowerKw > 0))
                {
                    errors.Add(new("market-export-requires-export", "grid_connections", "Market export requires an enabled export-capable grid connection."));
                }
                break;
            default:
                errors.Add(new("unknown-instrument", "available_instruments", $"Unsupported site instrument '{instrument}'."));
                break;
        }
    }

    private static void RequireAny<T>(
        IReadOnlyCollection<T> values,
        string code,
        List<SiteConfigurationValidationError> errors)
    {
        if (values.Count == 0)
        {
            errors.Add(new(code, "available_instruments", "Selected instrument is missing required site binding."));
        }
    }

    private static void RequireControllable(
        IReadOnlyList<SiteSourceRef> values,
        string code,
        List<SiteConfigurationValidationError> errors)
    {
        if (!values.Any(source => source.IsControllable))
        {
            errors.Add(new(code, "available_instruments", "Selected instrument requires a controllable source."));
        }
    }

    private static void ValidateRequiredText(
        string value,
        string field,
        string code,
        List<SiteConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(code, field, $"{field} is required."));
        }
    }

    private static void ValidatePositive(
        double value,
        string field,
        string code,
        List<SiteConfigurationValidationError> errors)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            errors.Add(new(code, field, $"{field} must be finite and positive."));
        }
    }
}

public sealed record SiteConfigurationValidationError(
    string Code,
    string Field,
    string Message);

public sealed record SiteConfigurationValidationResult(
    bool IsValid,
    IReadOnlyList<SiteConfigurationValidationError> Errors)
{
    public static readonly SiteConfigurationValidationResult Valid = new(true, []);

    public static SiteConfigurationValidationResult Invalid(
        IReadOnlyList<SiteConfigurationValidationError> errors) =>
        new(false, errors);
}
