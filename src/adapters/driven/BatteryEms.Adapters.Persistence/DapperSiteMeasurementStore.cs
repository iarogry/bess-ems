using BatteryEms.Application.Site;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperSiteMeasurementStore : ISiteMeasurementStore
{
    private const string UpsertSql = """
        INSERT INTO site_measurements (
            site_id,
            source,
            instrument_type,
            instrument_id,
            instrument_name,
            group_parent_id,
            scale,
            timestamp,
            interval_seconds,
            quality,
            metric,
            value,
            unit,
            metadata_json,
            imported_at)
        VALUES (
            @SiteId,
            @Source,
            @InstrumentType,
            @InstrumentId,
            @InstrumentName,
            @GroupParentId,
            @Scale,
            @Timestamp,
            @IntervalSeconds,
            @Quality,
            @Metric,
            @Value,
            @Unit,
            CAST(@MetadataJson AS jsonb),
            @ImportedAt)
        ON CONFLICT (site_id, source, instrument_type, instrument_id, timestamp, interval_seconds, metric)
        DO UPDATE SET
            instrument_name = EXCLUDED.instrument_name,
            group_parent_id = EXCLUDED.group_parent_id,
            scale = EXCLUDED.scale,
            interval_seconds = EXCLUDED.interval_seconds,
            quality = EXCLUDED.quality,
            value = EXCLUDED.value,
            unit = EXCLUDED.unit,
            metadata_json = EXCLUDED.metadata_json,
            imported_at = EXCLUDED.imported_at;
        """;

    private const string SelectSql = """
        SELECT
            site_id,
            source,
            instrument_type,
            instrument_id,
            instrument_name,
            group_parent_id,
            scale,
            timestamp,
            interval_seconds,
            quality,
            metric,
            value,
            unit,
            metadata_json
        FROM site_measurements
        WHERE site_id = @SiteId
          AND timestamp >= @From
          AND timestamp < @To
          AND (@Source IS NULL OR source = @Source)
          AND (@InstrumentType IS NULL OR instrument_type = @InstrumentType)
          AND (@InstrumentId IS NULL OR instrument_id = @InstrumentId)
          AND (@Metric IS NULL OR metric = @Metric)
        ORDER BY timestamp ASC, source ASC, instrument_id ASC, metric ASC;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperSiteMeasurementStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task AppendAsync(
        IReadOnlyList<SiteMeasurementReading> readings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readings);
        cancellationToken.ThrowIfCancellationRequested();
        if (readings.Count == 0)
        {
            return;
        }

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (var reading in readings)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        UpsertSql,
                        ToRow(reading.EnsureValid()),
                        transaction: transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<SiteMeasurementReading>> QueryAsync(
        SiteMeasurementQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.EnsureValid();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<MeasurementRow>(new CommandDefinition(
                SelectSql,
                new
                {
                    query.SiteId,
                    From = query.From.ToUniversalTime(),
                    To = query.To.ToUniversalTime(),
                    query.Source,
                    query.InstrumentType,
                    query.InstrumentId,
                    query.Metric,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return rows.Select(FromRow).ToArray();
        }
    }

    private static object ToRow(SiteMeasurementReading reading) => new
    {
        reading.SiteId,
        reading.Source,
        reading.InstrumentType,
        reading.InstrumentId,
        reading.InstrumentName,
        reading.GroupParentId,
        reading.Scale,
        Timestamp = reading.Timestamp.ToUniversalTime(),
        IntervalSeconds = reading.Interval is null ? 0 : (int)Math.Round(reading.Interval.Value.TotalSeconds),
        reading.Quality,
        reading.Metric,
        reading.Value,
        reading.Unit,
        MetadataJson = string.IsNullOrWhiteSpace(reading.MetadataJson) ? "{}" : reading.MetadataJson,
        ImportedAt = DateTimeOffset.UtcNow,
    };

    private static SiteMeasurementReading FromRow(MeasurementRow row) => new(
        row.SiteId,
        row.Source,
        row.InstrumentType,
        row.InstrumentId,
        row.InstrumentName,
        TimestampConverter.ToOffset(row.Timestamp),
        row.IntervalSeconds == 0 ? null : TimeSpan.FromSeconds(row.IntervalSeconds),
        row.Metric,
        row.Value,
        row.Unit,
        row.Quality,
        row.GroupParentId,
        row.Scale,
        row.MetadataJson);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class MeasurementRow
    {
        public string SiteId { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string InstrumentType { get; init; } = string.Empty;
        public string InstrumentId { get; init; } = string.Empty;
        public string InstrumentName { get; init; } = string.Empty;
        public string? GroupParentId { get; init; }
        public double? Scale { get; init; }
        public DateTime Timestamp { get; init; }
        public int IntervalSeconds { get; init; }
        public string Quality { get; init; } = string.Empty;
        public string Metric { get; init; } = string.Empty;
        public double? Value { get; init; }
        public string Unit { get; init; } = string.Empty;
        public string? MetadataJson { get; init; }
    }
}
