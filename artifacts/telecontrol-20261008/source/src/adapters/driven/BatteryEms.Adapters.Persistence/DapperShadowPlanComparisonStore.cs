using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperShadowPlanComparisonStore : IShadowPlanComparisonStore
{
    private const string InsertSnapshotSql = """
        INSERT INTO shadow_plan_snapshots (
            snapshot_id, site_id, delivery_date, side, recorded_at,
            payload_ready, snapshot_hash, snapshot_json)
        VALUES (
            @SnapshotId, @SiteId, @DeliveryDate, @Side, CURRENT_TIMESTAMP,
            @PayloadReady, @SnapshotHash, CAST(@SnapshotJson AS jsonb))
        ON CONFLICT (site_id, delivery_date, side, snapshot_hash) DO NOTHING;
        """;

    private const string SelectSnapshotSql = """
        SELECT snapshot_json::text AS SnapshotJson
        FROM shadow_plan_snapshots
        WHERE site_id = @SiteId
          AND delivery_date = @DeliveryDate
          AND side = @Side
        ORDER BY recorded_at DESC, snapshot_id DESC
        LIMIT 1;
        """;

    private const string UpsertComparisonSql = """
        INSERT INTO shadow_plan_comparisons (
            comparison_id, run_id, site_id, delivery_date, compared_at_utc,
            is_equivalent, legacy_payload_ready, shadow_payload_ready,
            mismatches_json)
        VALUES (
            @ComparisonId, @RunId, @SiteId, @DeliveryDate, @ComparedAtUtc,
            @IsEquivalent, @LegacyPayloadReady, @ShadowPayloadReady,
            CAST(@MismatchesJson AS jsonb))
        ON CONFLICT (run_id)
        DO UPDATE SET
            comparison_id = EXCLUDED.comparison_id,
            site_id = EXCLUDED.site_id,
            delivery_date = EXCLUDED.delivery_date,
            compared_at_utc = EXCLUDED.compared_at_utc,
            is_equivalent = EXCLUDED.is_equivalent,
            legacy_payload_ready = EXCLUDED.legacy_payload_ready,
            shadow_payload_ready = EXCLUDED.shadow_payload_ready,
            mismatches_json = EXCLUDED.mismatches_json;
        """;

    private const string SelectLatestComparisonSql = """
        SELECT *
        FROM shadow_plan_comparisons
        WHERE site_id = @SiteId
        ORDER BY compared_at_utc DESC, comparison_id DESC
        LIMIT 1;
        """;

    private const string SelectLatestComparisonForDateSql = """
        SELECT *
        FROM shadow_plan_comparisons
        WHERE site_id = @SiteId
          AND delivery_date = @DeliveryDate
        ORDER BY compared_at_utc DESC, comparison_id DESC
        LIMIT 1;
        """;

    private const string SelectComparisonRangeSql = """
        SELECT DISTINCT ON (delivery_date) *
        FROM shadow_plan_comparisons
        WHERE site_id = @SiteId
          AND delivery_date >= @FromDeliveryDate
          AND delivery_date <= @ToDeliveryDate
        ORDER BY delivery_date ASC, compared_at_utc DESC, comparison_id DESC;
        """;

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;

    public DapperShadowPlanComparisonStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        DapperConfig.EnsureConfigured();
        _dataSource = dataSource;
    }

    public async Task PutSnapshotAsync(
        ShadowPlanSide side,
        ShadowPlanSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot = snapshot.EnsureValid();
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertSnapshotSql,
                new
                {
                    SnapshotId = Guid.NewGuid(),
                    snapshot.SiteId,
                    snapshot.DeliveryDate,
                    Side = side.ToString(),
                    snapshot.PayloadReady,
                    SnapshotHash = hash,
                    SnapshotJson = json,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    public async Task<ShadowPlanSnapshot?> FindSnapshotAsync(
        ShadowPlanSide side,
        string siteId,
        DateOnly deliveryDate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                SelectSnapshotSql,
                new { SiteId = siteId, DeliveryDate = deliveryDate, Side = side.ToString() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (json is null)
            {
                return null;
            }

            return (JsonSerializer.Deserialize<ShadowPlanSnapshot>(json, SerializerOptions)
                    ?? throw new InvalidOperationException("Persisted shadow snapshot is empty."))
                .EnsureValid();
        }
    }

    public async Task SaveComparisonAsync(
        ShadowPlanComparisonRecord comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparison.SiteId);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertComparisonSql,
                new
                {
                    comparison.ComparisonId,
                    comparison.RunId,
                    comparison.SiteId,
                    comparison.DeliveryDate,
                    ComparedAtUtc = comparison.ComparedAtUtc.ToUniversalTime(),
                    comparison.IsEquivalent,
                    comparison.LegacyPayloadReady,
                    comparison.ShadowPayloadReady,
                    MismatchesJson = JsonSerializer.Serialize(
                        comparison.Mismatches,
                        SerializerOptions),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    public async Task<ShadowPlanComparisonRecord?> FindLatestComparisonAsync(
        string siteId,
        DateOnly? deliveryDate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var sql = deliveryDate is null
            ? SelectLatestComparisonSql
            : SelectLatestComparisonForDateSql;
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var row = await connection.QuerySingleOrDefaultAsync<ComparisonRow>(new CommandDefinition(
                sql,
                new { SiteId = siteId, DeliveryDate = deliveryDate },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row is null ? null : FromRow(row);
        }
    }

    public async Task<IReadOnlyList<ShadowPlanComparisonRecord>> QueryComparisonsAsync(
        string siteId,
        DateOnly fromDeliveryDate,
        DateOnly toDeliveryDate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        if (fromDeliveryDate > toDeliveryDate)
        {
            throw new ArgumentException("Comparison range start must not be after its end.");
        }

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<ComparisonRow>(new CommandDefinition(
                SelectComparisonRangeSql,
                new
                {
                    SiteId = siteId,
                    FromDeliveryDate = fromDeliveryDate,
                    ToDeliveryDate = toDeliveryDate,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(FromRow).ToArray();
        }
    }

    private static ShadowPlanComparisonRecord FromRow(ComparisonRow row) =>
        new(
            row.ComparisonId,
            row.RunId,
            row.SiteId,
            row.DeliveryDate,
            TimestampConverter.ToOffset(row.ComparedAtUtc),
            row.IsEquivalent,
            row.LegacyPayloadReady,
            row.ShadowPayloadReady,
            JsonSerializer.Deserialize<ShadowPlanMismatch[]>(
                row.MismatchesJson,
                SerializerOptions) ?? []);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812",
        Justification = "Instantiated by Dapper via reflection.")]
    private sealed class ComparisonRow
    {
        public Guid ComparisonId { get; init; }
        public Guid RunId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public DateOnly DeliveryDate { get; init; }
        public DateTime ComparedAtUtc { get; init; }
        public bool IsEquivalent { get; init; }
        public bool LegacyPayloadReady { get; init; }
        public bool ShadowPayloadReady { get; init; }
        public string MismatchesJson { get; init; } = "[]";
    }
}
