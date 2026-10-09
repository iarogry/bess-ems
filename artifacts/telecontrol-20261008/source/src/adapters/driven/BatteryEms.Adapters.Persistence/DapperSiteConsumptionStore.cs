using BatteryEms.Application.Site;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperSiteConsumptionStore : ISiteConsumptionStore
{
    private const string UpsertSql = """
        INSERT INTO site_consumption_readings (
            site_id,
            source,
            point_id,
            point_name,
            timestamp,
            apoz,
            aneg,
            ppoz,
            pneg,
            interval_seconds,
            metadata_json,
            imported_at)
        VALUES (
            @SiteId,
            @Source,
            @PointId,
            @PointName,
            @Timestamp,
            @Apoz,
            @Aneg,
            @Ppoz,
            @Pneg,
            @IntervalSeconds,
            @MetadataJson,
            @ImportedAt)
        ON CONFLICT (site_id, source, point_id, timestamp)
        DO UPDATE SET
            point_name = EXCLUDED.point_name,
            apoz = EXCLUDED.apoz,
            aneg = EXCLUDED.aneg,
            ppoz = EXCLUDED.ppoz,
            pneg = EXCLUDED.pneg,
            interval_seconds = EXCLUDED.interval_seconds,
            metadata_json = EXCLUDED.metadata_json,
            imported_at = EXCLUDED.imported_at;
        """;

    private const string SelectSql = """
        SELECT
            site_id,
            source,
            point_id,
            point_name,
            timestamp,
            apoz,
            aneg,
            ppoz,
            pneg,
            interval_seconds,
            metadata_json
        FROM site_consumption_readings
        WHERE site_id = @SiteId
          AND timestamp >= @From
          AND timestamp < @To
          AND (@PointId IS NULL OR point_id = @PointId)
          AND (@Source IS NULL OR source = @Source)
        ORDER BY timestamp ASC, point_id ASC;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public DapperSiteConsumptionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task AppendAsync(
        IReadOnlyList<SiteConsumptionReading> readings,
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
                        ToRow(reading),
                        transaction: transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<SiteConsumptionReading>> QueryAsync(
        SiteConsumptionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.EnsureValid();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<ConsumptionRow>(new CommandDefinition(
                SelectSql,
                new
                {
                    query.SiteId,
                    From = query.From.ToUniversalTime(),
                    To = query.To.ToUniversalTime(),
                    query.PointId,
                    query.Source,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return rows.Select(FromRow).ToArray();
        }
    }

    private static object ToRow(SiteConsumptionReading reading) => new
    {
        reading.SiteId,
        reading.Source,
        reading.PointId,
        reading.PointName,
        Timestamp = reading.Timestamp.ToUniversalTime(),
        reading.Apoz,
        reading.Aneg,
        reading.Ppoz,
        reading.Pneg,
        reading.IntervalSeconds,
        reading.MetadataJson,
        ImportedAt = DateTimeOffset.UtcNow,
    };

    private static SiteConsumptionReading FromRow(ConsumptionRow row) => new(
        row.SiteId,
        row.PointId,
        row.PointName,
        TimestampConverter.ToOffset(row.Timestamp),
        row.Apoz,
        row.Aneg,
        row.Ppoz,
        row.Pneg,
        row.Source,
        row.IntervalSeconds,
        row.MetadataJson);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated by Dapper via reflection.")]
    private sealed class ConsumptionRow
    {
        public string SiteId { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string PointId { get; init; } = string.Empty;
        public string PointName { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public double? Apoz { get; init; }
        public double? Aneg { get; init; }
        public double? Ppoz { get; init; }
        public double? Pneg { get; init; }
        public int? IntervalSeconds { get; init; }
        public string? MetadataJson { get; init; }
    }
}
