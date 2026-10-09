using BatteryEms.Adapters.Optimization.Deye;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Assets;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BatteryEms.Host.Planning;

public static class EmsPlanningRegistration
{
    public static void AddEmsPlanning(this IServiceCollection services, BessHostOptions host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(host);
        var options = host.Planning;
        if (!options.Enabled) { return; }
        Validate(host);
        services.AddSingleton(options);
        services.AddSingleton<IEquipmentDayPlanStore>(_ => new FileEquipmentDayPlanStore(options.PlanDirectory));
        services.AddSingleton<EmsDayAheadPlanner>();
        services.AddSingleton<IEquipmentDayAheadPlanningModel, BatteryDayAheadPlanningModel>();
        services.AddSingleton<IEquipmentScheduleCompiler>(sp => new DeyeEquipmentScheduleCompiler(options.Deye, sp.GetRequiredService<IBatteryAssetRegistry>()));
        foreach (var integration in options.Targets.Select(target => target.IntegrationId).Distinct(StringComparer.Ordinal))
        {
            if (integration == "deye_cloud") { continue; }
            services.AddSingleton<IEquipmentScheduleCompiler>(sp => new TrackedScheduleIntegration(integration, sp.GetRequiredService<IScheduleRepository>()));
            services.AddSingleton<IEquipmentPlanExecutor>(sp => new TrackedScheduleIntegration(integration, sp.GetRequiredService<IScheduleRepository>()));
        }
        if (options.DayAuthorizationsEnabled)
        { services.AddSingleton<IEquipmentPlanExecutor, DeyeDayPlanExecutor>(); }
        else { services.AddSingleton<IEquipmentPlanExecutor, DeyeScheduledPlanExecutor>(); }
        if (host.ShadowModeEnabled)
        { services.AddSingleton<IOrchestrationModule, PreparedDeyeShadowModule>(); }
        if (options.ActivationEnabled && options.Targets.Any(target => target.IntegrationId == "deye_cloud"))
        {
            services.RemoveAll<IActivationPlanDispatcher>();
            if (options.DayAuthorizationsEnabled)
            {
                services.AddHttpClient<IDeyeDayWindowDispatcher, HttpBrokerPlanDispatcher>(client => client.Timeout = TimeSpan.FromSeconds(20))
                    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            }
            services.AddHttpClient<IActivationPlanDispatcher, HttpBrokerPlanDispatcher>(client => client.Timeout = TimeSpan.FromSeconds(20))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        }
        services.AddSingleton<EmsPlanningHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<EmsPlanningHostedService>());
    }

    private static void Validate(BessHostOptions host)
    {
        var options = host.Planning;
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PlanDirectory);
        if (options.Targets.Count == 0 || options.Targets.Select(target => target.AssetId).Distinct(StringComparer.Ordinal).Count() != options.Targets.Count)
        { throw new InvalidOperationException("EMS planning requires unique equipment targets."); }
        if (host.ShadowSchedulerEnabled)
        { throw new InvalidOperationException("Disable the old ShadowScheduler when EMS planning owns the daily trigger."); }
        if (!string.Equals(host.PriceSeriesSource, "entso-e", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(host.ScheduleSolver.Backend, "or_tools", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("EMS automatic planning requires ENTSO-E and the telemetry-aware or_tools solver."); }
        foreach (var target in options.Targets) { ValidateTarget(target, host); }
        options.Deye.EnsureValid();
        ValidateActivation(host);
    }

    private static void ValidateTarget(EquipmentPlanningTarget target, BessHostOptions host)
    {
        var options = host.Planning;
        ArgumentException.ThrowIfNullOrWhiteSpace(target.AssetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.SiteId);
        if (target.ReserveSocPercent is { } reserve && (!double.IsFinite(reserve) || reserve < 0 || reserve >= 100))
        { throw new InvalidOperationException("Invalid EMS battery operating reserve."); }
        if (target.MarketBidArea != host.EntsoeDomainCode || target.PriceUnit is not ("UAH/MWh" or "EUR/MWh")
            || !double.IsFinite(target.ThroughputCostPerKwh) || target.ThroughputCostPerKwh < 0
            || target.IntegrationId is not ("deye_cloud" or "modbus" or "mqtt" or "opcua"))
        { throw new InvalidOperationException("Invalid EMS equipment planning target."); }
        if (target.IntegrationId == "deye_cloud" && options.Targets.Count(item => item.SiteId == target.SiteId && item.IntegrationId == "deye_cloud") != 1)
        { throw new InvalidOperationException("Deye site must map to one aggregate battery asset."); }
    }

    private static void ValidateActivation(BessHostOptions host)
    {
        var options = host.Planning;
        if (!options.Targets.Any(target => target.IntegrationId == "deye_cloud")) { return; }
        if (options.DayAuthorizationsEnabled && (!options.ActivationEnabled || string.IsNullOrWhiteSpace(options.WriterOwnerId)))
        { throw new InvalidOperationException("Daily Deye execution requires activation enabled and a configured broker writer owner."); }
        if (options.ActivationEnabled && (!host.ActivationPilotEnabled || !host.ActivationCutoverEnabled
            || string.IsNullOrWhiteSpace(host.PersistenceConnectionString)
            || (!options.DayAuthorizationsEnabled && string.IsNullOrWhiteSpace(options.ActivationBindingsDirectory))
            || options.BrokerBaseUrl is not { IsAbsoluteUri: true, Scheme: "https" }
            || string.IsNullOrWhiteSpace(options.BrokerToken)))
        { throw new InvalidOperationException("Scheduled Deye activation requires durable pilot/cutover, HTTPS broker credentials and approved execution scope."); }
    }
}
