using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BatteryEms.Api.Finance;

public static class SiteFinancialRegistration
{
    public static void AddSiteFinance(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var path = builder.Configuration["Bess:FinanceConfigPath"];
        if (!string.IsNullOrWhiteSpace(path))
        {
            builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: true);
        }
        builder.Services.AddSingleton<SiteFinancialPlanQuery>();
    }
}
