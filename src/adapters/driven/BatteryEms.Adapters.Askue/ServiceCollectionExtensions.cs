using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.Askue;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAskueSiteConsumption(
        this IServiceCollection services,
        IConfiguration configuration,
        string? defaultSiteId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<AskueOptions>()
            .Bind(configuration.GetSection(AskueOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.SiteId)
                    && !string.IsNullOrWhiteSpace(defaultSiteId))
                {
                    options.SiteId = defaultSiteId;
                }
            })
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.SiteId), "Askue:SiteId is required.")
            .ValidateOnStart();

        services.AddHttpClient(
            "Askue",
            (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<AskueOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

        services.TryAddTimeProvider();
        services.AddSingleton<AskueSiteConsumptionCollector>();
        services.AddHostedService<AskueConsumptionHostedService>();

        return services;
    }

    private static IServiceCollection TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(service => service.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }

        return services;
    }
}
