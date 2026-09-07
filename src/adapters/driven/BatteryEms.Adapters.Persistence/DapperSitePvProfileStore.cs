using BatteryEms.Application.Site;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperSitePvProfileStore : ISitePvProfileStore
{
    private const string UpsertSql = """
        INSERT INTO site_pv_profiles (
            site_id,
            pv_system_id,
            name,
            forecast_asset_id,
            enabled,
            latitude,
            longitude,
            tilt_degrees,
            azimuth_degrees,
            installed_dc_kw,
            inverter_ac_kw,
            temperature_coefficient_per_degree,
            system_loss_fraction,
            forecast_horizon_hours,
            forecast_resolution_minutes,
            forecast_provider,
            forecast_engine,
            notes,
            updated_at)
        VALUES (
            @SiteId,
            @PvSystemId,
            @Name,
            @ForecastAssetId,
            @Enabled,
            @Latitude,
            @Longitude,
            @TiltDegrees,
            @AzimuthDegrees,
            @InstalledDcKw,
            @InverterAcKw,
            @TemperatureCoefficientPerDegree,
            @SystemLossFraction,
            @ForecastHorizonHours,
            @ForecastResolutionMinutes,
            @ForecastProvider,
            @ForecastEngine,
            @Notes,
            @UpdatedAt)
        ON CONFLICT (site_id, pv_system_id)
        DO UPDATE SET
            name = EXCLUDED.name,
            forecast_asset_id = EXCLUDED.forecast_asset_id,
            enabled = EXCLUDED.enabled,
            latitude = EXCLUDED.latitude,
            longitude = EXCLUDED.longitude,
            tilt_degrees = EXCLUDED.tilt_degrees,
            azimuth_degrees = EXCLUDED.azimuth_degrees,
            installed_dc_kw = EXCLUDED.installed_dc_kw,
            inverter_ac_kw = EXCLUDED.inverter_ac_kw,
            temperature_coefficient_per_degree = EXCLUDED.temperature_coefficient_per_degree,
            system_loss_fraction = EXCLUDED.system_loss_fraction,
            forecast_horizon_hours = EXCLUDED.forecast_horizon_hours,
            forecast_resolution_minutes = EXCLUDED.forecast_resolution_minutes,
            forecast_provider = EXCLUDED.forecast_provider,
            forecast_engine = EXCLUDED.forecast_engine,
            notes = EXCLUDED.notes,
            updated_at = EXCLUDED.updated_at;
        """;

    private const string SelectOneSql = """
        SELECT
            site_id,
            pv_system_id,
            name,
            forecast_asset_id,
            enabled,
            latitude,
            longitude,
            tilt_degrees,
            azimuth_degrees,
            installed_dc_kw,
            inverter_ac_kw,
            temperature_coefficient_per_degree,
            system_loss_fraction,
            forecast_horizon_hours,
            forecast_resolution_minutes,
            forecast_provider,
            forecast_engine,
            notes
        FROM site_pv_profiles
        WHERE site_id = @SiteId
          AND pv_system_id = @PvSystemId;
        """;

    private const string SelectBySiteSql = """
        SELECT
            site_id,
            pv_system_id,
            name,
            forecast_asset_id,
            enabled,
            latitude,
            longitude,
            tilt_degrees,
            azimuth_degrees,
            installed_dc_kw,
            inverter_ac_kw,
            temperature_coefficient_per_degree,
            system_loss_fraction,
            forecast_horizon_hours,
            forecast_resolution_minutes,
            forecast_provider,
            forecast_engine,
            notes
        FROM site_pv_profiles
        WHERE site_id = @SiteId
        ORDER BY pv_system_id ASC;
        """;

    private const string SelectEnabledSql = """
        SELECT
            site_id,
            pv_system_id,
            name,
            forecast_asset_id,
            enabled,
            latitude,
            longitude,
            tilt_degrees,
            azimuth_degrees,
            installed_dc_kw,
            inverter_ac_kw,
            temperature_coefficient_per_degree,
            system_loss_fraction,
            forecast_horizon_hours,
            forecast_resolution_minutes,
            forecast_provider,
            forecast_engine,
            notes
        FROM site_pv_profiles
        WHERE enabled = TRUE
        ORDER BY site_id ASC, pv_system_id ASC;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperSitePvProfileStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(SitePvProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.EnsureValid();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertSql,
                new
                {
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
                    profile.Notes,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    public async Task<SitePvProfile?> FindAsync(string siteId, string pvSystemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pvSystemId);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
                SelectOneSql,
                new { SiteId = siteId, PvSystemId = pvSystemId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row is null ? null : row.ToModel();
        }
    }

    public async Task<IReadOnlyList<SitePvProfile>> ListBySiteAsync(string siteId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<Row>(new CommandDefinition(
                SelectBySiteSql,
                new { SiteId = siteId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(row => row.ToModel()).ToArray();
        }
    }

    public async Task<IReadOnlyList<SitePvProfile>> ListEnabledAsync(CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<Row>(new CommandDefinition(
                SelectEnabledSql,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(row => row.ToModel()).ToArray();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class Row
    {
        public string SiteId { get; init; } = string.Empty;
        public string PvSystemId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ForecastAssetId { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double TiltDegrees { get; init; }
        public double AzimuthDegrees { get; init; }
        public double InstalledDcKw { get; init; }
        public double InverterAcKw { get; init; }
        public double TemperatureCoefficientPerDegree { get; init; }
        public double SystemLossFraction { get; init; }
        public int ForecastHorizonHours { get; init; }
        public int ForecastResolutionMinutes { get; init; }
        public string ForecastProvider { get; init; } = string.Empty;
        public string ForecastEngine { get; init; } = string.Empty;
        public string? Notes { get; init; }

        public SitePvProfile ToModel() => new SitePvProfile(
            SiteId,
            PvSystemId,
            Name,
            ForecastAssetId,
            Enabled,
            Latitude,
            Longitude,
            TiltDegrees,
            AzimuthDegrees,
            InstalledDcKw,
            InverterAcKw,
            TemperatureCoefficientPerDegree,
            SystemLossFraction,
            ForecastHorizonHours,
            ForecastResolutionMinutes,
            ForecastProvider,
            ForecastEngine,
            Notes).EnsureValid();
    }
}
