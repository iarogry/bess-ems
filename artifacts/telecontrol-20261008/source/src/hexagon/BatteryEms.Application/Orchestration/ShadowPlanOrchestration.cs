using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BatteryEms.Application.Time;

namespace BatteryEms.Application.Orchestration;

public enum ShadowPlanSide
{
    Legacy,
    Shadow,
}

public sealed record LegacyPlanSnapshotLoadResult(
    ShadowPlanSnapshot? Snapshot,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Snapshot is not null;
}

public interface ILegacyPlanSnapshotSource
{
    Task<LegacyPlanSnapshotLoadResult> LoadAsync(
        string siteId,
        DateOnly deliveryDate,
        CancellationToken cancellationToken);
}

public sealed record ShadowPlanComparisonRecord(
    Guid ComparisonId,
    Guid RunId,
    string SiteId,
    DateOnly DeliveryDate,
    DateTimeOffset ComparedAtUtc,
    bool IsEquivalent,
    bool LegacyPayloadReady,
    bool ShadowPayloadReady,
    IReadOnlyList<ShadowPlanMismatch> Mismatches);

/// <summary>
/// Read-only migration boundary. Snapshot producers may publish legacy and
/// shadow observations, while the comparison module can only read them and
/// append comparison results. This port has no command or activation method.
/// </summary>
public interface IShadowPlanComparisonStore
{
    Task PutSnapshotAsync(
        ShadowPlanSide side,
        ShadowPlanSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<ShadowPlanSnapshot?> FindSnapshotAsync(
        ShadowPlanSide side,
        string siteId,
        DateOnly deliveryDate,
        CancellationToken cancellationToken);

    Task SaveComparisonAsync(
        ShadowPlanComparisonRecord comparison,
        CancellationToken cancellationToken);

    Task<ShadowPlanComparisonRecord?> FindLatestComparisonAsync(
        string siteId,
        DateOnly? deliveryDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShadowPlanComparisonRecord>> QueryComparisonsAsync(
        string siteId,
        DateOnly fromDeliveryDate,
        DateOnly toDeliveryDate,
        CancellationToken cancellationToken);
}

public sealed class InMemoryShadowPlanComparisonStore : IShadowPlanComparisonStore
{
    private readonly ConcurrentDictionary<SnapshotKey, ShadowPlanSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, ShadowPlanComparisonRecord> _comparisons = new();

    public Task PutSnapshotAsync(
        ShadowPlanSide side,
        ShadowPlanSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot = Freeze(snapshot.EnsureValid());
        _snapshots[new SnapshotKey(side, snapshot.SiteId, snapshot.DeliveryDate)] = snapshot;
        return Task.CompletedTask;
    }

    public Task<ShadowPlanSnapshot?> FindSnapshotAsync(
        ShadowPlanSide side,
        string siteId,
        DateOnly deliveryDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        _snapshots.TryGetValue(new SnapshotKey(side, siteId, deliveryDate), out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task SaveComparisonAsync(
        ShadowPlanComparisonRecord comparison,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparison.SiteId);
        _comparisons[comparison.ComparisonId] = comparison with
        {
            Mismatches = Array.AsReadOnly(comparison.Mismatches.ToArray()),
        };
        return Task.CompletedTask;
    }

    public Task<ShadowPlanComparisonRecord?> FindLatestComparisonAsync(
        string siteId,
        DateOnly? deliveryDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var comparison = _comparisons.Values
            .Where(item => string.Equals(item.SiteId, siteId, StringComparison.Ordinal)
                           && (deliveryDate is null || item.DeliveryDate == deliveryDate))
            .OrderByDescending(item => item.ComparedAtUtc)
            .ThenByDescending(item => item.ComparisonId)
            .FirstOrDefault();
        return Task.FromResult(comparison);
    }

    public Task<IReadOnlyList<ShadowPlanComparisonRecord>> QueryComparisonsAsync(
        string siteId,
        DateOnly fromDeliveryDate,
        DateOnly toDeliveryDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        if (fromDeliveryDate > toDeliveryDate)
        {
            throw new ArgumentException("Comparison range start must not be after its end.");
        }

        var comparisons = _comparisons.Values
            .Where(item => string.Equals(item.SiteId, siteId, StringComparison.Ordinal)
                           && item.DeliveryDate >= fromDeliveryDate
                           && item.DeliveryDate <= toDeliveryDate)
            .GroupBy(item => item.DeliveryDate)
            .Select(group => group
                .OrderByDescending(item => item.ComparedAtUtc)
                .ThenByDescending(item => item.ComparisonId)
                .First())
            .OrderBy(item => item.DeliveryDate)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ShadowPlanComparisonRecord>>(comparisons);
    }

    private sealed record SnapshotKey(ShadowPlanSide Side, string SiteId, DateOnly DeliveryDate);

    private static ShadowPlanSnapshot Freeze(ShadowPlanSnapshot snapshot) =>
        snapshot with
        {
            BlockingCodes = Array.AsReadOnly(snapshot.BlockingCodes.ToArray()),
            Windows = Array.AsReadOnly(
                snapshot.Windows
                    .Select(window => window with
                    {
                        Intervals = Array.AsReadOnly(window.Intervals.ToArray()),
                    })
                    .ToArray()),
        };
}

public sealed class ShadowPlanComparisonModule : IOrchestrationModule
{
    public const string SupportedRunType = "day-ahead-shadow";

    private readonly IShadowPlanComparisonStore _store;
    private readonly IClock _clock;

    public ShadowPlanComparisonModule(IShadowPlanComparisonStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _clock = clock;
    }

    public string ModuleName => "shadow_plan_comparison";

    public int StepOrder => 100;

    public async Task<OrchestrationModuleResult> ExecuteAsync(
        OrchestrationModuleContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.RunType, SupportedRunType, StringComparison.Ordinal))
        {
            return new OrchestrationModuleResult(OrchestrationStepStatus.Skipped);
        }

        if (!DateOnly.TryParseExact(
                context.TriggerRef,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var deliveryDate))
        {
            return Blocked("shadow-delivery-date-invalid", "TriggerRef must be the delivery date in yyyy-MM-dd form.");
        }

        var legacy = await _store.FindSnapshotAsync(
            ShadowPlanSide.Legacy,
            context.SiteId,
            deliveryDate,
            cancellationToken).ConfigureAwait(false);
        var shadow = await _store.FindSnapshotAsync(
            ShadowPlanSide.Shadow,
            context.SiteId,
            deliveryDate,
            cancellationToken).ConfigureAwait(false);
        if (legacy is null || shadow is null)
        {
            var missing = legacy is null && shadow is null
                ? "legacy,shadow"
                : legacy is null ? "legacy" : "shadow";
            return Blocked(
                "shadow-input-missing",
                "Legacy and shadow observations must both exist.",
                JsonSerializer.Serialize(new { missing }));
        }

        var result = ShadowPlanComparer.Compare(legacy, shadow);
        var comparison = new ShadowPlanComparisonRecord(
            context.RunId,
            context.RunId,
            context.SiteId,
            deliveryDate,
            _clock.UtcNow.ToUniversalTime(),
            result.IsEquivalent,
            legacy.PayloadReady,
            shadow.PayloadReady,
            result.Mismatches);
        await _store.SaveComparisonAsync(comparison, cancellationToken).ConfigureAwait(false);

        var metadata = JsonSerializer.Serialize(new
        {
            comparison_id = comparison.ComparisonId,
            equivalent = comparison.IsEquivalent,
            mismatch_count = comparison.Mismatches.Count,
        });
        return comparison.IsEquivalent
            ? new OrchestrationModuleResult(
                OrchestrationStepStatus.Succeeded,
                DataRole.Informational,
                DataBalanceState.Ready,
                OutputRef(comparison.ComparisonId),
                MetadataJson: metadata)
            : new OrchestrationModuleResult(
                OrchestrationStepStatus.Partial,
                DataRole.Advisory,
                DataBalanceState.Degraded,
                OutputRef(comparison.ComparisonId),
                "shadow-plan-mismatch",
                "Shadow output differs from the legacy reference.",
                metadata);
    }

    private static OrchestrationModuleResult Blocked(
        string errorCode,
        string errorMessage,
        string metadataJson = "{}") =>
        new(
            OrchestrationStepStatus.Blocked,
            DataRole.Informational,
            DataBalanceState.Blocked,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage,
            MetadataJson: metadataJson);

    private static string OutputRef(Guid comparisonId) =>
        $"shadow-comparison:{comparisonId:D}";
}

public sealed class LegacyPlanSnapshotImportModule : IOrchestrationModule
{
    private readonly ILegacyPlanSnapshotSource _source;
    private readonly IShadowPlanComparisonStore _store;

    public LegacyPlanSnapshotImportModule(
        ILegacyPlanSnapshotSource source,
        IShadowPlanComparisonStore store)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        _source = source;
        _store = store;
    }

    public string ModuleName => "legacy_plan_snapshot_import";

    public int StepOrder => 90;

    public async Task<OrchestrationModuleResult> ExecuteAsync(
        OrchestrationModuleContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(
                context.RunType,
                ShadowPlanComparisonModule.SupportedRunType,
                StringComparison.Ordinal))
        {
            return new OrchestrationModuleResult(OrchestrationStepStatus.Skipped);
        }

        if (!DateOnly.TryParseExact(
                context.TriggerRef,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var deliveryDate))
        {
            return Blocked("legacy-delivery-date-invalid", "TriggerRef must be the delivery date in yyyy-MM-dd form.");
        }

        var loaded = await _source.LoadAsync(
            context.SiteId,
            deliveryDate,
            cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Blocked(
                loaded.ErrorCode ?? "legacy-scenario-unavailable",
                loaded.ErrorMessage ?? "Legacy scenario is unavailable.");
        }

        await _store.PutSnapshotAsync(
            ShadowPlanSide.Legacy,
            loaded.Snapshot!,
            cancellationToken).ConfigureAwait(false);
        return new OrchestrationModuleResult(
            OrchestrationStepStatus.Succeeded,
            DataRole.Informational,
            DataBalanceState.Ready,
            OutputRef: $"legacy-shadow-snapshot:{context.SiteId}:{deliveryDate:yyyy-MM-dd}");
    }

    private static OrchestrationModuleResult Blocked(string errorCode, string errorMessage) =>
        new(
            OrchestrationStepStatus.Blocked,
            DataRole.Informational,
            DataBalanceState.Blocked,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage);
}
