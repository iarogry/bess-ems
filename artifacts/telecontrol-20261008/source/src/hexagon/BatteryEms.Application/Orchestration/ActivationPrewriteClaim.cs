using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatteryEms.Application.Orchestration;

public sealed record ActivationPrewriteClaim(
    Guid ClaimId,
    Guid OutboxItemId,
    Guid PilotSessionId,
    string SiteId,
    string WindowId,
    string WindowPayloadHash,
    string ExecutorId,
    string WriterOwnerId,
    long SafetyRevision,
    long FencingToken,
    string PayloadHash,
    string PayloadJson,
    DateTimeOffset ClaimedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(15);

    public ActivationPrewriteClaim EnsureValid()
    {
        if (ClaimId == Guid.Empty || OutboxItemId == Guid.Empty || PilotSessionId == Guid.Empty)
        {
            throw new ArgumentException("Claim, outbox and pilot identifiers must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        if (!ActivationPayloadIntegrity.IsWindowIdValid(WindowId))
        {
            throw new ArgumentException("Claim window must be one of Z1, Z2, Z3 or Z4.", nameof(WindowId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(WindowPayloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WriterOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PayloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(PayloadJson);
        if (SafetyRevision < 1 || FencingToken < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SafetyRevision));
        }

        var duration = ExpiresAtUtc - ClaimedAtUtc;
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpiresAtUtc),
                "Pre-write claim must be positive and no longer than 30 seconds.");
        }

        return this;
    }
}

public sealed record ActivationPrewriteClaimRequest(
    Guid ClaimId,
    Guid OutboxItemId,
    string ExecutorId,
    string WriterOwnerId,
    long ExpectedSafetyRevision,
    long ExpectedFencingToken,
    DateTimeOffset NowUtc)
{
    public ActivationPrewriteClaimRequest EnsureValid()
    {
        if (ClaimId == Guid.Empty || OutboxItemId == Guid.Empty)
        {
            throw new ArgumentException("Claim and outbox identifiers must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WriterOwnerId);
        if (ExpectedSafetyRevision < 1 || ExpectedFencingToken < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedSafetyRevision));
        }

        return this;
    }
}

public sealed record ActivationPrewriteClaimResult(
    ActivationPrewriteClaim? Claim,
    bool Claimed,
    string? ErrorCode = null,
    bool IsReplay = false);

public sealed record ActivationPrewriteCompletionRequest(
    Guid ClaimId,
    Guid OutboxItemId,
    string ExecutorId,
    bool Succeeded,
    string OutcomeCode,
    DateTimeOffset NowUtc)
{
    public ActivationPrewriteCompletionRequest EnsureValid()
    {
        if (ClaimId == Guid.Empty || OutboxItemId == Guid.Empty)
        {
            throw new ArgumentException("Claim and outbox identifiers must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutorId);
        new ActivationPlanDispatchResult(
            Succeeded ? ActivationDispatchOutcome.Succeeded : ActivationDispatchOutcome.Rejected,
            OutcomeCode).EnsureValid();
        return this;
    }
}

public sealed record ActivationPrewriteCompletionResult(
    ActivationOutboxStatus? Status,
    bool Completed,
    string? ErrorCode = null);

public interface IActivationPrewriteClaimStore
{
    Task<ActivationPrewriteClaimResult> TryClaimAsync(
        ActivationPrewriteClaimRequest request,
        CancellationToken cancellationToken);

    Task<ActivationPrewriteCompletionResult> CompleteAsync(
        ActivationPrewriteCompletionRequest request,
        CancellationToken cancellationToken);
}

public sealed class FailClosedActivationPrewriteClaimStore : IActivationPrewriteClaimStore
{
    public Task<ActivationPrewriteClaimResult> TryClaimAsync(
        ActivationPrewriteClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.EnsureValid();
        return Task.FromResult(new ActivationPrewriteClaimResult(
            null,
            Claimed: false,
            "activation-durable-persistence-required"));
    }

    public Task<ActivationPrewriteCompletionResult> CompleteAsync(
        ActivationPrewriteCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.EnsureValid();
        return Task.FromResult(new ActivationPrewriteCompletionResult(
            null,
            Completed: false,
            "activation-durable-persistence-required"));
    }
}

public static class ActivationPayloadIntegrity
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public static string ComputeHash(ShadowPlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot = snapshot.EnsureValid();
        var canonical = JsonSerializer.Serialize(snapshot, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputeWindowHash(ShadowTouWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(window.WindowId);
        if (window.Intervals.Count != 6)
        {
            throw new ArgumentException("A dispatch window must contain exactly six intervals.", nameof(window));
        }

        var canonical = JsonSerializer.Serialize(window, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool IsWindowIdValid(string? windowId) => windowId is "Z1" or "Z2" or "Z3" or "Z4";

    public static bool TryReadWindow(
        string payloadJson,
        string expectedHash,
        string windowId,
        out ShadowPlanSnapshot? snapshot,
        out ShadowTouWindow? window)
    {
        snapshot = null;
        window = null;
        if (!IsWindowIdValid(windowId)
            || !TryReadSnapshot(payloadJson, expectedHash, out snapshot)
            || snapshot is null)
        {
            return false;
        }

        var matching = snapshot.Windows.Where(candidate =>
            string.Equals(candidate.WindowId, windowId, StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1)
        {
            return false;
        }

        window = matching[0];
        return true;
    }

    public static bool IsValid(string payloadJson, string expectedHash)
        => TryReadSnapshot(payloadJson, expectedHash, out _);

    public static bool TryReadSnapshot(
        string payloadJson,
        string expectedHash,
        out ShadowPlanSnapshot? snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        snapshot = null;
        try
        {
            snapshot = JsonSerializer.Deserialize<ShadowPlanSnapshot>(
                payloadJson,
                SerializerOptions)?.EnsureValid();
            if (snapshot is null || !snapshot.PayloadReady)
            {
                snapshot = null;
                return false;
            }

            var actual = ComputeHash(snapshot);
            if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
            {
                snapshot = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            snapshot = null;
            return false;
        }
        catch (ArgumentException)
        {
            snapshot = null;
            return false;
        }
    }
}
