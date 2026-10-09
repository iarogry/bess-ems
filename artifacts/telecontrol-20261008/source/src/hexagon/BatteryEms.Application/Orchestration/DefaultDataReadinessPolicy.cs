namespace BatteryEms.Application.Orchestration;

public sealed class DefaultDataReadinessPolicy : IDataReadinessPolicy
{
    public DataReadinessResult Evaluate(
        IReadOnlyList<DataBalanceStatus> balances,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(balances);

        var blocking = balances
            .Where(balance => IsBlocking(balance, now))
            .ToArray();
        var degraded = balances
            .Where(balance => !blocking.Contains(balance))
            .Where(balance => IsDegraded(balance, now))
            .ToArray();

        return new DataReadinessResult(
            CanContinue: blocking.Length == 0,
            IsDegraded: degraded.Length > 0,
            BlockingCode: blocking.Length == 0 ? null : BuildBlockingCode(blocking[0]),
            BlockingBalances: blocking,
            DegradedBalances: degraded);
    }

    private static bool IsBlocking(DataBalanceStatus balance, DateTimeOffset now) =>
        balance.Role is DataRole.Critical or DataRole.BlockingSafety
        && (IsUnusable(balance.Status) || IsStaleByDeadline(balance, now));

    private static bool IsDegraded(DataBalanceStatus balance, DateTimeOffset now) =>
        balance.Role is DataRole.Advisory or DataRole.Informational
        && (IsUnusable(balance.Status) || IsStaleByDeadline(balance, now));

    private static bool IsUnusable(DataBalanceState status) =>
        status is DataBalanceState.Unknown
            or DataBalanceState.Missing
            or DataBalanceState.Stale
            or DataBalanceState.SourceError
            or DataBalanceState.Invalid
            or DataBalanceState.Blocked;

    private static bool IsStaleByDeadline(DataBalanceStatus balance, DateTimeOffset now) =>
        balance.FreshnessDeadlineUtc is { } deadline
        && deadline.ToUniversalTime() < now.ToUniversalTime();

    private static string BuildBlockingCode(DataBalanceStatus balance) =>
        balance.Role == DataRole.BlockingSafety
            ? "blocking-safety-data-not-ready"
            : $"{balance.DataGroup}-not-ready";
}
