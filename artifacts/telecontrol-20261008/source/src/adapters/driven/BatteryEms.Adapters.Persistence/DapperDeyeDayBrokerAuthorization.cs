using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

internal static class DapperDeyeDayBrokerAuthorization
{
    // The same query is used at admission, initiation, the mutation boundary
    // and saved-plan resolution. Revocation or proposal cancellation is live.
    private const string SelectSql = """
        SELECT p.payload_json::text AS PayloadJson, c.window_payload_hash AS WindowPayloadHash
        FROM deye_day_window_claims c
        JOIN deye_day_authorizations a ON a.authorization_id = c.authorization_id
        JOIN activation_proposals p ON p.proposal_id = a.proposal_id
        WHERE c.claim_id = @ActivationClaimId
            AND c.site_id = @SiteId AND c.delivery_date = @DeliveryDate
            AND c.window_id = @WindowId AND c.payload_hash = @PayloadHash
            AND c.writer_owner_id = @WriterOwnerId AND c.safety_revision = @SafetyRevision
            AND c.fencing_token = @FencingToken AND c.claimed_at_utc <= @NowUtc AND c.expires_at_utc > @NowUtc
            AND NOT a.revoked AND a.created_at_utc <= @NowUtc AND a.expires_at_utc > @NowUtc
            AND a.site_id = c.site_id AND a.delivery_date = c.delivery_date AND a.payload_hash = c.payload_hash
            AND a.writer_owner_id = c.writer_owner_id AND a.safety_revision = c.safety_revision
            AND p.status = 'Approved' AND p.site_id = a.site_id AND p.delivery_date = a.delivery_date
            AND p.payload_hash = a.payload_hash
        FOR UPDATE OF c, a;
        """;

    internal static async Task<ShadowPlanSnapshot?> ResolveAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, DeviceWriteBrokerBeginRequest request, CancellationToken token)
    {
        var row = await connection.QuerySingleOrDefaultAsync<PlanRow>(new CommandDefinition(SelectSql,
            new { request.ActivationClaimId, request.SiteId, request.DeliveryDate, request.WindowId,
                request.PayloadHash, request.WriterOwnerId, request.SafetyRevision, request.FencingToken,
                NowUtc = request.NowUtc.ToUniversalTime() }, transaction, cancellationToken: token)).ConfigureAwait(false);
        if (row is null || !ActivationPayloadIntegrity.TryReadWindow(row.PayloadJson, request.PayloadHash,
                request.WindowId, out var plan, out var window) || plan is null || window is null
            || plan.SiteId != request.SiteId || plan.DeliveryDate != request.DeliveryDate
            || ActivationPayloadIntegrity.ComputeWindowHash(window) != row.WindowPayloadHash)
        { return null; }
        return plan;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Dapper constructs the row by reflection.")]
    private sealed class PlanRow
    {
        public string PayloadJson { get; init; } = string.Empty;
        public string WindowPayloadHash { get; init; } = string.Empty;
    }
}
