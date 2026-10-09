using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperDeyeDayAuthorizationStore : IDeyeDayAuthorizationStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly NpgsqlDataSource _dataSource;

    public DapperDeyeDayAuthorizationStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        DapperConfig.EnsureConfigured();
    }

    public async Task<DeyeDayAuthorizationResult> AuthorizeAsync(DeyeDayAuthorizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var proposal = await ReadAsync<ActivationProposal>(connection, transaction, """
            SELECT (to_jsonb(p) || jsonb_build_object('payload_json', p.payload_json::text))::text
            FROM activation_proposals p WHERE proposal_id = @ProposalId FOR UPDATE;
            """, new { request.ProposalId }, cancellationToken).ConfigureAwait(false);
        var blocking = ValidateProposal(proposal, request, out var expires);
        if (blocking is not null || proposal is null) { return new(null, false, blocking); }
        var equivalent = await connection.ExecuteScalarAsync<bool>(Command("""
            SELECT EXISTS(SELECT 1 FROM shadow_plan_comparisons
                WHERE comparison_id = @ComparisonId AND site_id = @SiteId AND delivery_date = @DeliveryDate
                    AND is_equivalent AND legacy_payload_ready AND shadow_payload_ready
                    AND mismatches_json = '[]'::jsonb AND compared_at_utc <= @NowUtc);
            """, new { proposal.ComparisonId, proposal.SiteId, proposal.DeliveryDate, NowUtc = request.NowUtc.ToUniversalTime() },
            transaction, cancellationToken)).ConfigureAwait(false);
        if (!equivalent) { return new(null, false, "deye-day-equivalent-shadow-required"); }
        var existing = await ReadAsync<DeyeDayAuthorization>(connection, transaction, """
            SELECT to_jsonb(a)::text FROM deye_day_authorizations a
            WHERE site_id = @SiteId AND delivery_date = @DeliveryDate FOR UPDATE;
            """, new { proposal.SiteId, proposal.DeliveryDate }, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.ProposalId == request.ProposalId && existing.WriterOwnerId == request.WriterOwnerId
                && existing.SafetyRevision == request.SafetyRevision && !existing.Revoked
                ? new(existing, true) : new(existing, false, "deye-day-authorization-conflict");
        }
        if (!await ValidateSafetyAsync(connection, transaction, proposal.SiteId, request.WriterOwnerId,
            request.SafetyRevision, null, request.NowUtc, cancellationToken).ConfigureAwait(false))
        { return new(null, false, "deye-day-writer-safety-invalid"); }
        await connection.ExecuteAsync(Command("""
            INSERT INTO deye_day_authorizations(authorization_id, proposal_id, site_id, delivery_date,
                payload_hash, writer_owner_id, safety_revision, authorized_by, reason, created_at_utc, expires_at_utc)
            VALUES(@AuthorizationId, @ProposalId, @SiteId, @DeliveryDate, @PayloadHash, @WriterOwnerId,
                @SafetyRevision, @Actor, @Reason, @NowUtc, @ExpiresAtUtc);
            """, new { request.AuthorizationId, request.ProposalId, proposal.SiteId, proposal.DeliveryDate,
                proposal.PayloadHash, request.WriterOwnerId, request.SafetyRevision, request.Actor, request.Reason,
                NowUtc = request.NowUtc.ToUniversalTime(), ExpiresAtUtc = expires }, transaction, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(new(request.AuthorizationId, request.ProposalId, proposal.SiteId, proposal.DeliveryDate,
            proposal.PayloadHash, request.WriterOwnerId, request.SafetyRevision, request.NowUtc.ToUniversalTime(), expires, false), true);
    }

    private static string? ValidateProposal(ActivationProposal? proposal, DeyeDayAuthorizationRequest request, out DateTimeOffset expires)
    {
        expires = default;
        if (proposal is null || proposal.Status != ActivationProposalStatus.Approved
            || proposal.ReviewedBy != request.Actor || proposal.ProposedBy == request.Actor
            || proposal.ReviewedAtUtc is null || proposal.ReviewedAtUtc > request.NowUtc
            || proposal.CreatedAtUtc > request.NowUtc || proposal.ExpiresAtUtc <= request.NowUtc)
        { return "deye-day-independent-approval-required"; }
        if (!ActivationPayloadIntegrity.TryReadWindow(proposal.PayloadJson, proposal.PayloadHash, "Z1", out var plan, out _)
            || plan is null || !plan.PayloadReady || plan.SiteId != proposal.SiteId || plan.DeliveryDate != proposal.DeliveryDate)
        { return "deye-day-approved-payload-invalid"; }
        var first = ActivationWindowTimingPolicy.Evaluate(plan.DeliveryDate, "Z1", request.NowUtc);
        if (first.ScheduledAtUtc is null || request.NowUtc >= first.ScheduledAtUtc)
        { return "deye-day-approval-after-first-trigger"; }
        // Explicit daily scope extends the reviewed proposal only to this
        // delivery day's final trigger deadline, never to another day.
        var last = ActivationWindowTimingPolicy.Evaluate(plan.DeliveryDate, "Z4", request.NowUtc);
        if (last.StartDeadlineUtc is null) { return "deye-day-timing-unsupported"; }
        expires = last.StartDeadlineUtc.Value;
        return null;
    }

    public async Task<DeyeDayAuthorization?> FindAsync(string siteId, DateOnly deliveryDate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = connection.ConfigureAwait(false);
        return await ReadAsync<DeyeDayAuthorization>(connection, null, """
            SELECT to_jsonb(a)::text FROM deye_day_authorizations a WHERE site_id = @SiteId AND delivery_date = @DeliveryDate;
            """, new { SiteId = siteId, DeliveryDate = deliveryDate }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(Guid authorizationId, string actor, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (authorizationId == Guid.Empty) { throw new ArgumentException("Authorization ID is required."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = connection.ConfigureAwait(false);
        var count = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE deye_day_authorizations SET revoked = TRUE, revoked_by = @Actor,
                revoke_reason = @Reason, revoked_at_utc = @Now
            WHERE authorization_id = @AuthorizationId AND NOT revoked AND created_at_utc <= @Now;
            """, new { AuthorizationId = authorizationId, Actor = actor, Reason = reason, Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count == 1;
    }

    public async Task<DeyeDayWindowClaimResult> ClaimAsync(DeyeDayWindowClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timing = ActivationWindowTimingPolicy.Evaluate(request.DeliveryDate, request.WindowId, request.NowUtc);
        if (!timing.CanStart) { return new(null, false, BlockingCode: timing.BlockingCode); }
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var authorization = await ReadAsync<DeyeDayAuthorization>(connection, transaction, """
            SELECT to_jsonb(a)::text FROM deye_day_authorizations a
            WHERE site_id = @SiteId AND delivery_date = @DeliveryDate FOR UPDATE;
            """, new { request.SiteId, request.DeliveryDate }, cancellationToken).ConfigureAwait(false);
        if (authorization is null || authorization.Revoked || authorization.CreatedAtUtc > request.NowUtc
            || authorization.ExpiresAtUtc <= request.NowUtc || authorization.PayloadHash != request.PayloadHash
            || authorization.WriterOwnerId != request.WriterOwnerId || authorization.SafetyRevision != request.SafetyRevision)
        { return new(null, false, BlockingCode: "deye-day-authorization-not-live"); }
        var existing = await ReadAsync<DeyeDayWindowClaim>(connection, transaction, """
            SELECT to_jsonb(c)::text FROM deye_day_window_claims c
            WHERE site_id = @SiteId AND delivery_date = @DeliveryDate AND window_id = @WindowId;
            """, new { request.SiteId, request.DeliveryDate, request.WindowId }, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.WriterOwnerId == request.WriterOwnerId && existing.FencingToken == request.FencingToken
                && existing.SafetyRevision == request.SafetyRevision && existing.PayloadHash == request.PayloadHash
                ? new(existing, true, IsReplay: true)
                : new(null, false, BlockingCode: "deye-day-claim-replay-mismatch");
        }
        if (!await ValidateSafetyAsync(connection, transaction, request.SiteId, request.WriterOwnerId,
            request.SafetyRevision, request.FencingToken, request.NowUtc, cancellationToken).ConfigureAwait(false))
        { return new(null, false, BlockingCode: "deye-day-writer-safety-invalid"); }
        var proposal = await ReadAsync<ActivationProposal>(connection, transaction, """
            SELECT (to_jsonb(p) || jsonb_build_object('payload_json', p.payload_json::text))::text
            FROM activation_proposals p WHERE proposal_id = @ProposalId;
            """, new { authorization.ProposalId }, cancellationToken).ConfigureAwait(false);
        if (proposal?.Status != ActivationProposalStatus.Approved || proposal.PayloadHash != authorization.PayloadHash
            || !ActivationPayloadIntegrity.TryReadWindow(proposal.PayloadJson, request.PayloadHash, request.WindowId, out _, out var window)
            || window is null)
        { return new(null, false, BlockingCode: "deye-day-approved-payload-invalid"); }
        var expires = request.NowUtc.AddSeconds(30);
        if (expires > timing.StartDeadlineUtc!.Value) { expires = timing.StartDeadlineUtc.Value; }
        var claim = new DeyeDayWindowClaim(Guid.NewGuid(), authorization.AuthorizationId, request.SiteId,
            request.DeliveryDate, request.WindowId, request.PayloadHash, ActivationPayloadIntegrity.ComputeWindowHash(window),
            request.WriterOwnerId, request.SafetyRevision, request.FencingToken, request.NowUtc.ToUniversalTime(), expires);
        await InsertClaimAsync(connection, transaction, claim, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(claim, true);
    }

    private static Task<int> InsertClaimAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeyeDayWindowClaim claim, CancellationToken token) => connection.ExecuteAsync(Command("""
            INSERT INTO deye_day_window_claims(claim_id, authorization_id, site_id, delivery_date,
                window_id, payload_hash, window_payload_hash, writer_owner_id, safety_revision,
                fencing_token, claimed_at_utc, expires_at_utc)
            VALUES(@ClaimId, @AuthorizationId, @SiteId, @DeliveryDate, @WindowId, @PayloadHash,
                @WindowPayloadHash, @WriterOwnerId, @SafetyRevision, @FencingToken, @ClaimedAtUtc, @ExpiresAtUtc);
            """, claim, transaction, token));

    private static async Task<bool> ValidateSafetyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string siteId, string owner, long revision, long? fencing, DateTimeOffset now, CancellationToken token)
    {
        await connection.ExecuteAsync(Command("""
            SELECT site_id FROM activation_writer_safety WHERE site_id = @SiteId FOR UPDATE;
            SELECT site_id FROM activation_writer_leases WHERE site_id = @SiteId FOR UPDATE;
            """, new { SiteId = siteId }, transaction, token)).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(Command("""
            SELECT EXISTS(SELECT 1 FROM activation_writer_safety s
            WHERE s.site_id = @SiteId AND NOT s.kill_switch_engaged AND s.writer_authority = 'ProductAgent'
                AND s.revision = @Revision AND s.updated_at_utc <= @Now
                AND s.legacy_writer_stopped_at_utc <= @Now AND length(trim(s.legacy_stop_evidence)) > 0
                AND (@Fencing IS NULL OR EXISTS(SELECT 1 FROM activation_writer_leases l
                    WHERE l.site_id = s.site_id AND l.owner_id = @Owner AND l.fencing_token = @Fencing
                        AND l.acquired_at_utc <= @Now AND l.expires_at_utc > @Now)));
            """, new { SiteId = siteId, Owner = owner, Revision = revision, Fencing = fencing, Now = now.ToUniversalTime() },
            transaction, token)).ConfigureAwait(false);
    }

    private static async Task<T?> ReadAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string sql, object parameters, CancellationToken token)
    {
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(sql, parameters,
            transaction, cancellationToken: token)).ConfigureAwait(false);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static CommandDefinition Command(string sql, object parameters, NpgsqlTransaction transaction, CancellationToken token) =>
        new(sql, parameters, transaction, cancellationToken: token);
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
