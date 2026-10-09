using System.Data;
using BatteryEms.Application.Orchestration;
using Dapper;
using Npgsql;

namespace BatteryEms.Adapters.Persistence;

public sealed class DapperDeviceWriteBrokerAttemptStore : IDeviceWriteBrokerAttemptStore, IDeviceWriteBrokerMutationGate
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _dayAuthorizationsEnabled;

    public DapperDeviceWriteBrokerAttemptStore(NpgsqlDataSource dataSource, bool dayAuthorizationsEnabled = false)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _dayAuthorizationsEnabled = dayAuthorizationsEnabled;
        DapperConfig.EnsureConfigured();
    }

    public async Task<DeviceWriteBrokerAttemptResult> BeginAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        await LockSiteAsync(connection, transaction, request.SiteId, cancellationToken).ConfigureAwait(false);
        var existing = await FindAttemptAsync(connection, transaction, request.AttemptId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Matches(existing, request)
                ? Accepted(existing.State, replay: true)
                : Blocked("device-write-broker-attempt-conflict");
        }
        var blocked = await FindPriorAttemptBlockAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
        blocked ??= await ValidateAuthorityAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
        if (blocked is not null) { return Blocked(blocked); }
        await connection.ExecuteAsync(Command("""
            INSERT INTO device_write_broker_attempts (
                attempt_id, site_id, delivery_date, window_id, payload_hash, authority,
                writer_owner_id, safety_revision, fencing_token, activation_claim_id, state, begun_at_utc)
            VALUES (@AttemptId, @SiteId, @DeliveryDate, @WindowId, @PayloadHash, @Authority,
                @WriterOwnerId, @SafetyRevision, @FencingToken, @ActivationClaimId, 'Prepared', @NowUtc);
            """, new
        {
            request.AttemptId, request.SiteId, request.DeliveryDate, request.WindowId, request.PayloadHash,
            Authority = request.Authority.ToString(), request.WriterOwnerId, request.SafetyRevision,
            request.FencingToken, request.ActivationClaimId, NowUtc = request.NowUtc.ToUniversalTime(),
        }, transaction, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Accepted("Prepared");
    }

    public async Task<DeviceWriteBrokerAttemptResult> MarkInitiatedAsync(
        Guid attemptId, string writerOwnerId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ValidateIdentity(attemptId, writerOwnerId);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var row = await FindLockedAttemptAsync(connection, transaction, attemptId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.WriterOwnerId != writerOwnerId) { return Blocked("device-write-broker-attempt-mismatch"); }
        if (row.State == "Initiated") { return Accepted(row.State, replay: true); }
        if (row.State != "Prepared") { return Blocked("device-write-broker-attempt-not-prepared"); }
        if (nowUtc.UtcDateTime < row.BegunAtUtc) { return Blocked("device-write-broker-clock-before-attempt"); }
        var blocked = await ValidateAuthorityAsync(connection, transaction, ToRequest(row, nowUtc), cancellationToken).ConfigureAwait(false);
        if (blocked is not null) { return Blocked(blocked); }
        await connection.ExecuteAsync(Command("""
            UPDATE device_write_broker_attempts SET state = 'Initiated', initiated_at_utc = @NowUtc
            WHERE attempt_id = @AttemptId AND state = 'Prepared';
            """, new { AttemptId = attemptId, NowUtc = nowUtc.ToUniversalTime() }, transaction, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Accepted("Initiated");
    }

    public async Task<bool> CanSendAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var row = await FindLockedAttemptAsync(connection, transaction, request.AttemptId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.State != "Initiated" || !Matches(row, request)
            || row.InitiatedAtUtc is null || row.InitiatedAtUtc > request.NowUtc.UtcDateTime) { return false; }
        var blocked = await ValidateAuthorityAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return blocked is null;
    }

    public async Task<DeviceWriteBrokerAttemptResult> ObserveAsync(
        Guid attemptId, string writerOwnerId, DeviceWriteBrokerObservation observation, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ValidateIdentity(attemptId, writerOwnerId);
        if (!Enum.IsDefined(observation)) { throw new ArgumentOutOfRangeException(nameof(observation)); }
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var row = await FindLockedAttemptAsync(connection, transaction, attemptId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.WriterOwnerId != writerOwnerId) { return Blocked("device-write-broker-attempt-mismatch"); }
        var target = observation.ToString();
        if (row.State == target) { return Accepted(row.State, replay: true); }
        var blocked = ObservationBlock(row, observation, nowUtc);
        if (blocked is not null) { return Blocked(blocked); }
        var code = observation switch
        {
            DeviceWriteBrokerObservation.Verified => "device-write-broker-readback-matched",
            DeviceWriteBrokerObservation.NotSent => "device-write-broker-not-sent",
            _ => "device-write-broker-outcome-unknown",
        };
        await connection.ExecuteAsync(Command("""
            UPDATE device_write_broker_attempts
            SET state = @State, observed_at_utc = @NowUtc, outcome_code = @Code
            WHERE attempt_id = @AttemptId;
            """, new { State = target, NowUtc = nowUtc.ToUniversalTime(), Code = code, AttemptId = attemptId }, transaction, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Accepted(target);
    }

    private static string? ObservationBlock(AttemptRow row, DeviceWriteBrokerObservation observation, DateTimeOffset nowUtc)
    {
        if (row.State is "Unknown" or "Verified" or "NotSent") { return "device-write-broker-reconciliation-required"; }
        if (nowUtc.UtcDateTime < (row.InitiatedAtUtc ?? row.BegunAtUtc)) { return "device-write-broker-clock-before-attempt"; }
        return observation switch
        {
            DeviceWriteBrokerObservation.NotSent when row.State != "Prepared" => "device-write-broker-mutation-may-have-started",
            DeviceWriteBrokerObservation.Verified when row.State != "Initiated" => "device-write-broker-mutation-not-initiated",
            _ => null,
        };
    }

    private static async Task<string?> FindPriorAttemptBlockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DeviceWriteBrokerBeginRequest request, CancellationToken token)
    {
        var open = await connection.ExecuteScalarAsync<bool>(Command("""
            SELECT EXISTS(SELECT 1 FROM device_write_broker_attempts
                WHERE site_id = @SiteId AND state IN ('Prepared', 'Initiated', 'Unknown'));
            """, new { request.SiteId }, transaction, token)).ConfigureAwait(false);
        if (open) { return "device-write-broker-unresolved-attempt"; }
        var consumed = await connection.ExecuteScalarAsync<bool>(Command("""
            SELECT EXISTS(SELECT 1 FROM device_write_broker_attempts
                WHERE activation_claim_id = @ActivationClaimId
                   OR (site_id = @SiteId AND delivery_date = @DeliveryDate AND window_id = @WindowId AND state = 'Verified'));
            """, new { request.ActivationClaimId, request.SiteId, request.DeliveryDate, request.WindowId }, transaction, token)).ConfigureAwait(false);
        if (consumed) { return "device-write-broker-authorization-consumed"; }
        var notSentCount = await connection.ExecuteScalarAsync<long>(Command("""
            SELECT COUNT(*) FROM device_write_broker_attempts
            WHERE site_id = @SiteId AND delivery_date = @DeliveryDate AND window_id = @WindowId AND state = 'NotSent';
            """, new { request.SiteId, request.DeliveryDate, request.WindowId }, transaction, token)).ConfigureAwait(false);
        return notSentCount >= 3 ? "device-write-broker-prewrite-attempt-limit" : null;
    }

    private async Task<string?> ValidateAuthorityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DeviceWriteBrokerBeginRequest request, CancellationToken token)
    {
        var timing = ActivationWindowTimingPolicy.Evaluate(request.DeliveryDate, request.WindowId, request.NowUtc);
        if (!timing.CanStart) { return timing.BlockingCode; }
        if (request.Authority == ActivationWriterAuthority.ProductAgent)
        {
            var claimRow = await connection.QuerySingleOrDefaultAsync<Guid?>(Command("""
                SELECT outbox_item_id FROM activation_outbox
                    WHERE claim_id = @ActivationClaimId AND status = 'Claimed'
                      AND site_id = @SiteId AND delivery_date = @DeliveryDate
                      AND claim_window_id = @WindowId AND payload_hash = @PayloadHash
                      AND release_writer_owner_id = @WriterOwnerId
                      AND claim_safety_revision = @SafetyRevision AND claim_fencing_token = @FencingToken
                      AND claimed_at_utc <= @NowUtc AND claim_expires_at_utc > @NowUtc
                FOR UPDATE;
                """, Parameters(request), transaction, token)).ConfigureAwait(false);
            if (claimRow is null && (!_dayAuthorizationsEnabled
                || await DapperDeyeDayBrokerAuthorization.ResolveAsync(connection, transaction, request, token).ConfigureAwait(false) is null))
            { return "device-write-broker-product-claim-invalid"; }
        }
        var state = await connection.QuerySingleOrDefaultAsync<SafetyRow>(Command("""
            SELECT kill_switch_engaged, writer_authority, revision, updated_at_utc,
                   legacy_writer_stopped_at_utc, legacy_stop_evidence
            FROM activation_writer_safety WHERE site_id = @SiteId FOR UPDATE;
            """, new { request.SiteId }, transaction, token)).ConfigureAwait(false);
        if (state is null) { return "activation-safety-state-missing"; }
        if (state.KillSwitchEngaged) { return "activation-kill-switch-engaged"; }
        if (state.UpdatedAtUtc > request.NowUtc.UtcDateTime) { return "device-write-broker-clock-before-authority"; }
        if (state.Revision != request.SafetyRevision || state.WriterAuthority != request.Authority.ToString())
        {
            return "device-write-broker-authority-mismatch";
        }
        if (request.Authority == ActivationWriterAuthority.ProductAgent
            && (state.LegacyWriterStoppedAtUtc is null
                || state.LegacyWriterStoppedAtUtc > request.NowUtc.UtcDateTime
                || string.IsNullOrWhiteSpace(state.LegacyStopEvidence)))
        {
            return "activation-legacy-writer-stop-unproven";
        }
        var lease = await connection.QuerySingleOrDefaultAsync<LeaseRow>(Command("""
            SELECT owner_id, fencing_token, acquired_at_utc, expires_at_utc
            FROM activation_writer_leases WHERE site_id = @SiteId FOR UPDATE;
            """, new { request.SiteId }, transaction, token)).ConfigureAwait(false);
        if (lease is null || lease.OwnerId != request.WriterOwnerId || lease.FencingToken != request.FencingToken
            || lease.AcquiredAtUtc > request.NowUtc.UtcDateTime || lease.ExpiresAtUtc <= request.NowUtc.UtcDateTime)
        {
            return "device-write-broker-lease-invalid";
        }
        return null;
    }

    private static object Parameters(DeviceWriteBrokerBeginRequest request) => new
    {
        request.ActivationClaimId, request.SiteId, request.DeliveryDate, request.WindowId,
        request.PayloadHash, request.WriterOwnerId, request.SafetyRevision, request.FencingToken,
        NowUtc = request.NowUtc.ToUniversalTime(),
    };

    private static void ValidateIdentity(Guid attemptId, string ownerId)
    {
        if (attemptId == Guid.Empty) { throw new ArgumentException("Attempt identifier is required."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
    }

    private static DeviceWriteBrokerAttemptResult Accepted(string state, bool replay = false) =>
        new(true, replay, Enum.Parse<DeviceWriteBrokerAttemptState>(state));
    private static DeviceWriteBrokerAttemptResult Blocked(string code) => new(false, BlockingCode: code);
    private static CommandDefinition Command(string sql, object parameters, NpgsqlTransaction transaction, CancellationToken token) =>
        new(sql, parameters, transaction, cancellationToken: token);

    private static async Task LockSiteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteId, CancellationToken token)
    {
        await connection.ExecuteAsync(Command("""
            INSERT INTO device_write_broker_sites(site_id) VALUES (@SiteId) ON CONFLICT DO NOTHING;
            """, new { SiteId = siteId }, transaction, token)).ConfigureAwait(false);
        await connection.QuerySingleAsync<string>(Command("""
            SELECT site_id FROM device_write_broker_sites WHERE site_id = @SiteId FOR UPDATE;
            """, new { SiteId = siteId }, transaction, token)).ConfigureAwait(false);
    }

    private static Task<AttemptRow?> FindAttemptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid attemptId, CancellationToken token) =>
        connection.QuerySingleOrDefaultAsync<AttemptRow>(Command("SELECT * FROM device_write_broker_attempts WHERE attempt_id = @AttemptId;",
            new { AttemptId = attemptId }, transaction, token));

    private static async Task<AttemptRow?> FindLockedAttemptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid attemptId, CancellationToken token)
    {
        var row = await FindAttemptAsync(connection, transaction, attemptId, token).ConfigureAwait(false);
        if (row is null) { return null; }
        await LockSiteAsync(connection, transaction, row.SiteId, token).ConfigureAwait(false);
        return await FindAttemptAsync(connection, transaction, attemptId, token).ConfigureAwait(false);
    }

    private static bool Matches(AttemptRow row, DeviceWriteBrokerBeginRequest request) =>
        row.SiteId == request.SiteId && row.DeliveryDate == request.DeliveryDate && row.WindowId == request.WindowId
        && row.PayloadHash == request.PayloadHash && row.Authority == request.Authority.ToString()
        && row.WriterOwnerId == request.WriterOwnerId && row.SafetyRevision == request.SafetyRevision
        && row.FencingToken == request.FencingToken && row.ActivationClaimId == request.ActivationClaimId;

    private static DeviceWriteBrokerBeginRequest ToRequest(AttemptRow row, DateTimeOffset nowUtc) =>
        new(row.AttemptId, row.SiteId, row.DeliveryDate, row.WindowId, row.PayloadHash,
            Enum.Parse<ActivationWriterAuthority>(row.Authority), row.WriterOwnerId, row.SafetyRevision,
            row.FencingToken, row.ActivationClaimId, nowUtc);

    private sealed class AttemptRow
    {
        public Guid AttemptId { get; init; }
        public string SiteId { get; init; } = string.Empty;
        public DateOnly DeliveryDate { get; init; }
        public string WindowId { get; init; } = string.Empty;
        public string PayloadHash { get; init; } = string.Empty;
        public string Authority { get; init; } = string.Empty;
        public string WriterOwnerId { get; init; } = string.Empty;
        public long SafetyRevision { get; init; }
        public long FencingToken { get; init; }
        public Guid? ActivationClaimId { get; init; }
        public string State { get; init; } = string.Empty;
        public DateTime BegunAtUtc { get; init; }
        public DateTime? InitiatedAtUtc { get; init; }
    }
    private sealed class SafetyRow
    {
        public bool KillSwitchEngaged { get; init; }
        public string WriterAuthority { get; init; } = string.Empty;
        public long Revision { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
        public DateTime? LegacyWriterStoppedAtUtc { get; init; }
        public string? LegacyStopEvidence { get; init; }
    }
    private sealed class LeaseRow
    {
        public string OwnerId { get; init; } = string.Empty;
        public long FencingToken { get; init; }
        public DateTime AcquiredAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
    }
}
