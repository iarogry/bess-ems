using System.Collections.Concurrent;

namespace BatteryEms.Application.Site;

public sealed class InMemorySiteRegistry : ISiteRegistry
{
    private readonly ConcurrentDictionary<string, SiteDescriptor> _sites = new(StringComparer.Ordinal);

    public InMemorySiteRegistry(IEnumerable<SiteDescriptor>? seed = null)
    {
        if (seed is null)
        {
            return;
        }

        foreach (var site in seed)
        {
            Register(site);
        }
    }

    public void Register(SiteDescriptor site)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(site.SiteId);
        _sites[site.SiteId] = site;
    }

    public SiteDescriptor? Find(string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        return _sites.TryGetValue(siteId, out var site) ? site : null;
    }

    public IReadOnlyList<SiteDescriptor> All() =>
        _sites.Values.OrderBy(site => site.SiteId, StringComparer.Ordinal).ToArray();
}
