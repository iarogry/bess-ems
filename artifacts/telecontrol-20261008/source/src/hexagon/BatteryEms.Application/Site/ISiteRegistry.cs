namespace BatteryEms.Application.Site;

public interface ISiteRegistry
{
    SiteDescriptor? Find(string siteId);

    IReadOnlyList<SiteDescriptor> All();
}
