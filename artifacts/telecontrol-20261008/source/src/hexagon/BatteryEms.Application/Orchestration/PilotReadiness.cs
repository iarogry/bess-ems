namespace BatteryEms.Application.Orchestration;

public sealed record PilotReadinessOptions(
    int RequiredConsecutiveDays = 14,
    int MaximumQueryDays = 90)
{
    public PilotReadinessOptions EnsureValid()
    {
        if (RequiredConsecutiveDays < 7 || RequiredConsecutiveDays > MaximumQueryDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RequiredConsecutiveDays),
                RequiredConsecutiveDays,
                "Required consecutive days must be between 7 and the maximum query window.");
        }

        if (MaximumQueryDays is < 7 or > 366)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumQueryDays));
        }

        return this;
    }
}

public sealed record PilotReadinessDay(
    DateOnly DeliveryDate,
    bool EvidencePresent,
    bool IsEquivalent,
    bool LegacyPayloadReady,
    bool ShadowPayloadReady,
    int MismatchCount,
    string? BlockingCode);

public sealed record PilotReadinessResult(
    string SiteId,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    int RequiredDays,
    int PassingDays,
    bool IsReady,
    IReadOnlyList<PilotReadinessDay> Days)
{
    public IReadOnlyList<string> BlockingCodes => Days
        .Where(day => day.BlockingCode is not null)
        .Select(day => day.BlockingCode!)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}

public static class PilotReadinessPolicy
{
    public static PilotReadinessResult Evaluate(
        string siteId,
        DateOnly windowEnd,
        PilotReadinessOptions options,
        IReadOnlyList<ShadowPlanComparisonRecord> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evidence);
        options = options.EnsureValid();
        var windowStart = windowEnd.AddDays(-(options.RequiredConsecutiveDays - 1));
        var latestByDate = evidence
            .Where(item => string.Equals(item.SiteId, siteId, StringComparison.Ordinal)
                           && item.DeliveryDate >= windowStart
                           && item.DeliveryDate <= windowEnd)
            .GroupBy(item => item.DeliveryDate)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.ComparedAtUtc)
                    .ThenByDescending(item => item.ComparisonId)
                    .First());
        var days = new List<PilotReadinessDay>(options.RequiredConsecutiveDays);
        for (var date = windowStart; date <= windowEnd; date = date.AddDays(1))
        {
            if (!latestByDate.TryGetValue(date, out var comparison))
            {
                days.Add(new PilotReadinessDay(
                    date,
                    EvidencePresent: false,
                    IsEquivalent: false,
                    LegacyPayloadReady: false,
                    ShadowPayloadReady: false,
                    MismatchCount: 0,
                    "pilot-evidence-missing-day"));
                continue;
            }

            var blockingCode = BlockingCode(comparison);
            days.Add(new PilotReadinessDay(
                date,
                EvidencePresent: true,
                comparison.IsEquivalent,
                comparison.LegacyPayloadReady,
                comparison.ShadowPayloadReady,
                comparison.Mismatches.Count,
                blockingCode));
        }

        var passingDays = days.Count(day => day.BlockingCode is null);
        return new PilotReadinessResult(
            siteId,
            windowStart,
            windowEnd,
            options.RequiredConsecutiveDays,
            passingDays,
            IsReady: passingDays == options.RequiredConsecutiveDays,
            days.AsReadOnly());
    }

    private static string? BlockingCode(ShadowPlanComparisonRecord comparison)
    {
        if (!comparison.LegacyPayloadReady || !comparison.ShadowPayloadReady)
        {
            return "pilot-payload-not-ready";
        }

        if (!comparison.IsEquivalent || comparison.Mismatches.Count != 0)
        {
            return "pilot-plan-mismatch";
        }

        return null;
    }
}

public interface IPilotReadinessUseCase
{
    Task<PilotReadinessResult> EvaluateAsync(
        string siteId,
        DateOnly windowEnd,
        CancellationToken cancellationToken);
}

public sealed class DefaultPilotReadinessUseCase : IPilotReadinessUseCase
{
    private readonly IShadowPlanComparisonStore _comparisons;
    private readonly PilotReadinessOptions _options;

    public DefaultPilotReadinessUseCase(
        IShadowPlanComparisonStore comparisons,
        PilotReadinessOptions options)
    {
        _comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).EnsureValid();
    }

    public async Task<PilotReadinessResult> EvaluateAsync(
        string siteId,
        DateOnly windowEnd,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var windowStart = windowEnd.AddDays(-(_options.RequiredConsecutiveDays - 1));
        var evidence = await _comparisons.QueryComparisonsAsync(
            siteId,
            windowStart,
            windowEnd,
            cancellationToken).ConfigureAwait(false);
        return PilotReadinessPolicy.Evaluate(siteId, windowEnd, _options, evidence);
    }
}
