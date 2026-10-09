using BatteryEms.Application.Site;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.Askue;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAskueSiteConsumption(
        this IServiceCollection services, IConfiguration configuration, string? defaultSiteId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISiteConsumptionPollStatusStore, InMemorySiteConsumptionPollStatusStore>();
        var accounts = configuration.GetSection("Askue:Accounts");
        if (!accounts.Exists())
        {
            RegisterAccount(services, configuration.GetSection("Askue"), Options.DefaultName, "Askue", defaultSiteId);
            services.AddSingleton<AskueSiteConsumptionCollector>();
            services.AddHostedService<AskueConsumptionHostedService>();
            return services;
        }

        var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts.GetChildren().Where(section => section.GetValue("Enabled", true)))
        {
            var site = account["SiteId"]?.Trim();
            if (string.IsNullOrWhiteSpace(site) || !sites.Add(site))
            {
                throw new OptionsValidationException(account.Key, typeof(AskueOptions),
                    ["Each enabled ASKUE account requires an explicit, unique SiteId."]);
            }
            var name = account.Key;
            RegisterAccount(services, account, name, "Askue:" + name, null);
            services.AddKeyedSingleton<AskueSiteConsumptionCollector>(name, (sp, _) => new(
                sp.GetRequiredService<IHttpClientFactory>(),
                Options.Create(sp.GetRequiredService<IOptionsMonitor<AskueOptions>>().Get(name)),
                sp.GetRequiredService<ISiteConsumptionStore>(),
                sp.GetRequiredService<ILogger<AskueSiteConsumptionCollector>>()));
            services.AddSingleton<IHostedService>(sp => new AskueConsumptionHostedService(
                sp.GetRequiredKeyedService<AskueSiteConsumptionCollector>(name),
                Options.Create(sp.GetRequiredService<IOptionsMonitor<AskueOptions>>().Get(name)),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AskueConsumptionHostedService>>(),
                sp.GetRequiredService<ISiteConsumptionPollStatusStore>()));
        }
        return services;
    }

    private static void RegisterAccount(IServiceCollection services, IConfiguration section,
        string optionsName, string clientName, string? defaultSiteId)
    {
        services.AddOptions<AskueOptions>(optionsName).Bind(section)
            .PostConfigure(options =>
            {
                options.SiteId = (string.IsNullOrWhiteSpace(options.SiteId) ? defaultSiteId : options.SiteId)?.Trim() ?? string.Empty;
                options.AccountId = optionsName.Length == 0 ? "default" : optionsName;
                options.HttpClientName = clientName;
            })
            .ValidateDataAnnotations()
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo), "ASKUE BaseUrl must be an HTTP(S) URL without credentials.")
            .ValidateOnStart();
        services.AddHttpClient(clientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<AskueOptions>>().Get(optionsName);
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("bess-ems-askue-backfill/1.0");
            client.DefaultRequestHeaders.Add("Origin", "https://askue.net");
            client.DefaultRequestHeaders.Referrer = new Uri("https://askue.net/show/content.html");
            client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
    }
}
