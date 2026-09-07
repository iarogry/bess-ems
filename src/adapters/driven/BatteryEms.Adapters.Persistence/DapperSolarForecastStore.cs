using BatteryEms.Application.Forecasting;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperSolarForecastStore : ISolarForecastStore
{
    private const string UpsertHeaderSql = """
        INSERT INTO solar_forecasts (
            asset_id,
            source,
            model,
            generated_at,
            horizon_start,
            horizon_end,
            time_step_seconds,
            installed_dc_kw,
            installed_ac_kw,
            updated_at)
        VALUES (
            @AssetId,
            @Source,
            @Model,
            @GeneratedAt,
            @HorizonStart,
            @HorizonEnd,
            @TimeStepSeconds,
            @InstalledDcKw,
            @InstalledAcKw,
            @UpdatedAt)
        ON CONFLICT (asset_id)
        DO UPDATE SET
            source = EXCLUDED.source,
            model = EXCLUDED.model,
            generated_at = EXCLUDED.generated_at,
            horizon_start = EXCLUDED.horizon_start,
            horizon_end = EXCLUDED.horizon_end,
            time_step_seconds = EXCLUDED.time_step_seconds,
            installed_dc_kw = EXCLUDED.installed_dc_kw,
            installed_ac_kw = EXCLUDED.installed_ac_kw,
            updated_at = EXCLUDED.updated_at;
        """;

    private const string DeletePointsSql = """
        DELETE FROM solar_forecast_points
        WHERE asset_id = @AssetId;
        """;

    private const string InsertPointSql = """
        INSERT INTO solar_forecast_points (
            asset_id,
            generated_at,
            timestamp,
            power_kw,
            irradiance_w_per_square_meter,
            ambient_temperature_celsius,
            wind_speed_meters_per_second,
            cloud_cover_percent)
        VALUES (
            @AssetId,
            @GeneratedAt,
            @Timestamp,
            @PowerKw,
            @IrradianceWPerSquareMeter,
            @AmbientTemperatureCelsius,
            @WindSpeedMetersPerSecond,
            @CloudCoverPercent);
        """;

    private const string SelectHeaderSql = """
        SELECT
            asset_id,
            source,
            model,
            generated_at,
            horizon_start,
            horizon_end,
            time_step_seconds,
            installed_dc_kw,
            installed_ac_kw
        FROM solar_forecasts
        WHERE asset_id = @AssetId;
        """;

    private const string SelectPointsSql = """
        SELECT
            timestamp,
            power_kw,
            irradiance_w_per_square_meter,
            ambient_temperature_celsius,
            wind_speed_meters_per_second,
            cloud_cover_percent
        FROM solar_forecast_points
        WHERE asset_id = @AssetId
        ORDER BY timestamp ASC;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperSolarForecastStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public void Update(SolarForecast forecast) =>
        UpdateAsync(forecast, CancellationToken.None).GetAwaiter().GetResult();

    public SolarForecast? GetLatest(string assetId) =>
        GetLatestAsync(assetId, CancellationToken.None).GetAwaiter().GetResult();

    public async Task UpdateAsync(SolarForecast forecast, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(forecast);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    UpsertHeaderSql,
                    new
                    {
                        forecast.AssetId,
                        forecast.Source,
                        forecast.Model,
                        GeneratedAt = forecast.GeneratedAt.ToUniversalTime(),
                        HorizonStart = forecast.HorizonStart.ToUniversalTime(),
                        HorizonEnd = forecast.HorizonEnd.ToUniversalTime(),
                        TimeStepSeconds = (int)Math.Round(forecast.TimeStep.TotalSeconds),
                        forecast.InstalledDcKw,
                        InstalledAcKw = forecast.InstalledAcKw,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition(
                    DeletePointsSql,
                    new { forecast.AssetId },
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                foreach (var point in forecast.Points)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        InsertPointSql,
                        new
                        {
                            forecast.AssetId,
                            GeneratedAt = forecast.GeneratedAt.ToUniversalTime(),
                            Timestamp = point.Timestamp.ToUniversalTime(),
                            point.PowerKw,
                            point.IrradianceWPerSquareMeter,
                            point.AmbientTemperatureCelsius,
                            point.WindSpeedMetersPerSecond,
                            point.CloudCoverPercent,
                        },
                        transaction: transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<SolarForecast?> GetLatestAsync(string assetId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var header = await connection.QuerySingleOrDefaultAsync<HeaderRow>(new CommandDefinition(
                SelectHeaderSql,
                new { AssetId = assetId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (header is null)
            {
                return null;
            }

            var points = await connection.QueryAsync<PointRow>(new CommandDefinition(
                SelectPointsSql,
                new { AssetId = assetId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return new SolarForecast(
                assetId: header.AssetId,
                source: header.Source,
                model: header.Model,
                generatedAt: TimestampConverter.ToOffset(header.GeneratedAt),
                horizonStart: TimestampConverter.ToOffset(header.HorizonStart),
                horizonEnd: TimestampConverter.ToOffset(header.HorizonEnd),
                timeStep: TimeSpan.FromSeconds(header.TimeStepSeconds),
                installedDcKw: header.InstalledDcKw,
                installedAcKw: header.InstalledAcKw,
                points: points.Select(row => new SolarForecastPoint(
                    Timestamp: TimestampConverter.ToOffset(row.Timestamp),
                    PowerKw: row.PowerKw,
                    IrradianceWPerSquareMeter: row.IrradianceWPerSquareMeter,
                    AmbientTemperatureCelsius: row.AmbientTemperatureCelsius,
                    WindSpeedMetersPerSecond: row.WindSpeedMetersPerSecond,
                    CloudCoverPercent: row.CloudCoverPercent).EnsureValid()).ToArray());
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class HeaderRow
    {
        public string AssetId { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public DateTime GeneratedAt { get; init; }
        public DateTime HorizonStart { get; init; }
        public DateTime HorizonEnd { get; init; }
        public int TimeStepSeconds { get; init; }
        public double InstalledDcKw { get; init; }
        public double InstalledAcKw { get; init; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class PointRow
    {
        public DateTime Timestamp { get; init; }
        public double PowerKw { get; init; }
        public double IrradianceWPerSquareMeter { get; init; }
        public double AmbientTemperatureCelsius { get; init; }
        public double WindSpeedMetersPerSecond { get; init; }
        public int CloudCoverPercent { get; init; }
    }
}
