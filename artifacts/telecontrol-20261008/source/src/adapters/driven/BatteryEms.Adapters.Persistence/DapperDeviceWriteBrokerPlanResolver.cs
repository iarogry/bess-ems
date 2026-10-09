using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

// Resolves product writes exclusively from the released, claimed outbox row.
// Legacy snapshots are observational data, not signed write authorization;
// this resolver intentionally refuses to turn one into a device payload.
public sealed class DapperDeviceWriteBrokerPlanResolver : IDeviceWriteBrokerPlanResolver
{
    private const string SelectProductPlanSql = """
        SELECT payload_json::text AS PayloadJson,
               claim_window_payload_hash AS WindowPayloadHash
        FROM activation_outbox
        WHERE claim_id = @ActivationClaimId
          AND status = 'Claimed'
          AND site_id = @SiteId
          AND delivery_date = @DeliveryDate
          AND payload_hash = @PayloadHash
          AND release_writer_owner_id = @WriterOwnerId
          AND release_window_id = @WindowId
          AND claim_window_id = @WindowId
          AND claim_safety_revision = @SafetyRevision
          AND claim_fencing_token = @FencingToken
          AND claimed_at_utc <= @NowUtc
          AND claim_expires_at_utc > @NowUtc;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _dayAuthorizationsEnabled;

    public DapperDeviceWriteBrokerPlanResolver(NpgsqlDataSource dataSource, bool dayAuthorizationsEnabled = false)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _dayAuthorizationsEnabled = dayAuthorizationsEnabled;
        DapperConfig.EnsureConfigured();
    }

    public async Task<ShadowPlanSnapshot?> ResolveAsync(
        DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        if (request.Authority != ActivationWriterAuthority.ProductAgent)
        {
            return null;
        }

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var row = await connection.QuerySingleOrDefaultAsync<ProductPlanRow>(new CommandDefinition(
                SelectProductPlanSql,
                new
                {
                    request.ActivationClaimId, request.SiteId, request.DeliveryDate, request.PayloadHash,
                    request.WriterOwnerId, request.WindowId, request.SafetyRevision, request.FencingToken,
                    NowUtc = request.NowUtc.ToUniversalTime(),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (row is null && _dayAuthorizationsEnabled)
            {
                var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                var daily = await DapperDeyeDayBrokerAuthorization.ResolveAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return daily;
            }
            if (row is null || row.WindowPayloadHash is null
                || !ActivationPayloadIntegrity.TryReadWindow(row.PayloadJson, request.PayloadHash,
                    request.WindowId, out var plan, out var window)
                || plan is null || window is null
                || !string.Equals(plan.SiteId, request.SiteId, StringComparison.Ordinal)
                || plan.DeliveryDate != request.DeliveryDate
                || !string.Equals(ActivationPayloadIntegrity.ComputeWindowHash(window),
                    row.WindowPayloadHash, StringComparison.Ordinal))
            {
                return null;
            }

            return plan;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
        Justification = "Dapper constructs this persisted row by reflection.")]
    private sealed class ProductPlanRow
    {
        public string PayloadJson { get; init; } = string.Empty;
        public string? WindowPayloadHash { get; init; }
    }
}
