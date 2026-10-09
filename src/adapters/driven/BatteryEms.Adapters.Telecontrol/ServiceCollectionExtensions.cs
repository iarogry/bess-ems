using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.Telecontrol;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelecontrolTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetValue<bool>("Telecontrol:Enabled")) { return services; }
        services.AddOptions<TelecontrolOptions>().Bind(configuration.GetSection(TelecontrolOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(value => IsHttps(value.AuthBaseUrl) && IsHttps(value.GatewayBaseUrl), "Telecontrol endpoints must be HTTPS base URLs ending in /.")
            .Validate(value => TimeZoneInfo.TryFindSystemTimeZoneById(value.SourceTimeZoneId, out _), "Telecontrol source timezone must exist.")
            .ValidateOnStart();
        services.AddHttpClient("Telecontrol", (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<TelecontrolOptions>>().Value.HttpTimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("bess-ems-telecontrol/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TelecontrolClient>();
        services.AddHostedService<TelecontrolPollingService>();
        return services;
    }

    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath.EndsWith('/');
}
