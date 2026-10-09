using BatteryEms.Application.Site;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BatteryEms.Api.Composition;

public static class SiteCatalogRegistration
{
    public static IServiceCollection AddConfiguredSiteCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISiteRegistry>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var entries = configuration.GetSection("Sites:Catalog").GetChildren().ToArray();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sites = new List<SiteDescriptor>();
            foreach (var entry in entries)
            {
                var id = entry["SiteId"]?.Trim();
                var name = entry["Name"]?.Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !ids.Add(id))
                {
                    throw new InvalidOperationException("Site catalog requires unique nonempty SiteId and Name.");
                }
                var bindings = configuration.GetSection("Dashboard:Sites").GetChildren()
                    .Where(site => string.Equals(site["SiteId"], id, StringComparison.Ordinal))
                    .SelectMany(site => site.GetSection("Sources").GetChildren())
                    .Where(source => source["Kind"] == "battery")
                    .Select(source => source["TelemetryId"])
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                    .Distinct(StringComparer.Ordinal).ToArray();
                sites.Add(new SiteDescriptor(id, name, entry["TimeZone"] ?? "Europe/Kyiv",
                    entry["MarketBidArea"] ?? string.Empty, [], bindings, [], [], [], new SiteInstrumentSet([])));
            }
            return new InMemorySiteRegistry(sites);
        });
        return services;
    }
}
