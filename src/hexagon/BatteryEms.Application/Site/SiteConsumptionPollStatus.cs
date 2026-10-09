using System.Collections.Concurrent;

namespace BatteryEms.Application.Site;

public sealed record SiteConsumptionPollStatus(string AccountId, string SiteId, string State,
    DateTimeOffset LastAttemptAt, DateTimeOffset? LastSuccessAt, int? LastImportedReadings, string? ErrorCode);

public interface ISiteConsumptionPollStatusStore
{
    void Started(string accountId, string siteId, DateTimeOffset at);
    void Succeeded(string accountId, string siteId, DateTimeOffset at, int count);
    void Failed(string accountId, string siteId, DateTimeOffset at, string errorCode);
    IReadOnlyList<SiteConsumptionPollStatus> All();
}

public sealed class InMemorySiteConsumptionPollStatusStore : ISiteConsumptionPollStatusStore
{
    private readonly ConcurrentDictionary<string, SiteConsumptionPollStatus> _accounts = new(StringComparer.Ordinal);
    public void Started(string accountId, string siteId, DateTimeOffset at) => Update(accountId, siteId, "polling", at, null, null);
    public void Succeeded(string accountId, string siteId, DateTimeOffset at, int count) => Update(accountId, siteId, "ok", at, count, null);
    public void Failed(string accountId, string siteId, DateTimeOffset at, string errorCode) => Update(accountId, siteId, "failed", at, null, errorCode);
    public IReadOnlyList<SiteConsumptionPollStatus> All() => _accounts.Values.OrderBy(item => item.SiteId, StringComparer.Ordinal).ToArray();
    private void Update(string accountId, string siteId, string state, DateTimeOffset at, int? count, string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        _accounts.AddOrUpdate(accountId,
            _ => new SiteConsumptionPollStatus(accountId, siteId, state, at, count is null ? null : at, count, error),
            (_, old) => new SiteConsumptionPollStatus(accountId, siteId, state,
                state == "polling" ? at : old.LastAttemptAt,
                count is null ? old.LastSuccessAt : at, count ?? old.LastImportedReadings, error));
    }
}
