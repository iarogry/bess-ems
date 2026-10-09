using BatteryEms.Adapters.Askue;
using BatteryEms.Adapters.DeyeCloud;
using BatteryEms.Adapters.Entsoe;
using BatteryEms.Adapters.FusionSolar;
using BatteryEms.Adapters.Telecontrol;
using BatteryEms.Adapters.Modbus;
using BatteryEms.Adapters.Mqtt;
using BatteryEms.Adapters.NativeInterop;
using BatteryEms.Adapters.OpcUa;
using BatteryEms.Adapters.Optimization;
using BatteryEms.Adapters.Optimization.OrTools;
using BatteryEms.Adapters.OptimizationCore;
using BatteryEms.Adapters.Persistence;
using BatteryEms.Adapters.Telemetry.OpenTelemetry;
using BatteryEms.Adapters.Telemetry.Prometheus;
using BatteryEms.Api.Auth;
using BatteryEms.Api.Composition;
using BatteryEms.Api.Endpoints;
using BatteryEms.Api.Finance;
using BatteryEms.Api.Observability;
using BatteryEms.Api.Scheduling;
using BatteryEms.Application.Assets;
using BatteryEms.Application.IO;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Mpc;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Persistence;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Time;
using BatteryEms.Infrastructure.Time;
using BatteryEms.Worker;
using BatteryEms.Host.Planning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

namespace BatteryEms.Host;

// Composition root for the bess-ems process. Wires together the API
// driving adapter, the Worker driving adapter, the Telemetry/Prometheus
// driven adapter, the Persistence driven adapter (when configured) and
// the Optimization driven adapter. NoOp telemetry source / command sink
// are the default for headless smokes — RM-M1-19c will swap in the
// Modbus/MQTT adapters once the mapping loaders are wired.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506", Justification = "Composition root intentionally wires every host adapter and cross-cutting service.")]
public static class BessHostBuilder
{
    public static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddSiteFinance();
        var hostOptions = LoadHostOptions(builder.Configuration);
        var runtimeConfig = BessConfigurationBootstrap.Load(hostOptions);

        ConfigureHostBuilder(builder, hostOptions, runtimeConfig);
        var app = builder.Build();
        ConfigureApp(app, hostOptions, runtimeConfig);
        return app;
    }

    private static BessHostOptions LoadHostOptions(ConfigurationManager configuration) =>
        configuration
            .GetSection(BessHostOptions.SectionName)
            .Get<BessHostOptions>() ?? new BessHostOptions();

    private static void ConfigureHostBuilder(
        WebApplicationBuilder builder,
        BessHostOptions hostOptions,
        BessRuntimeConfiguration runtimeConfig)
    {
        builder.Host.ConfigureBessJsonLogging();
        ConfigureJson(builder.Services);
        builder.Services.AddOpenApi();
        builder.Services.AddBessApplicationInMemoryStores(
            hostOptions.BatterySnapshotMaxAge,
            hostOptions.SiteTelemetrySnapshotMaxAge);
        builder.Services.AddSingleton(new PilotReadinessOptions(
            hostOptions.PilotReadinessRequiredDays).EnsureValid());
        builder.Services.AddBessNativeControl(builder.Configuration);
        ConfigurePersistence(builder.Services, hostOptions);
        ConfigurePriceSeriesSource(builder.Services, hostOptions);
        ConfigureSiteTelemetry(builder.Services, hostOptions, runtimeConfig, builder.Configuration);
        builder.Services.AddTelecontrolTelemetry(builder.Configuration);
        ConfigureSiteConsumption(builder.Services, hostOptions, runtimeConfig, builder.Configuration);
        SolarForecastRegistration.Configure(builder.Services, hostOptions, runtimeConfig, builder.Configuration);
        ConfigureOptimization(builder.Services, hostOptions);
        builder.Services.AddEmsPlanning(hostOptions);
        builder.Services.AddBessTelemetry();
        builder.Services.AddBessTracing();
        if (runtimeConfig.Assets.Count == 1)
        {
            builder.Services.AddSingleton(runtimeConfig.SingleAsset);
        }
        ConfigureIoAdapters(builder.Services, hostOptions, runtimeConfig, builder.Configuration);
        builder.Services.AddBessWorker(builder.Configuration);
        builder.Services.AddApiTokenAuth(builder.Configuration);
        builder.Services.AddSingleton(runtimeConfig);
    }

    private static void ConfigureJson(IServiceCollection services)
    {
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
        });
    }

    private static void ConfigurePersistence(IServiceCollection services, BessHostOptions hostOptions)
    {
        if (!string.IsNullOrWhiteSpace(hostOptions.PersistenceConnectionString))
        {
            services.AddBessPersistence(hostOptions.PersistenceConnectionString!);
        }
    }

    private static void ConfigureOptimization(IServiceCollection services, BessHostOptions hostOptions)
    {
        services.AddBessOptimization();
        if (hostOptions.ShadowModeEnabled)
        {
            if (string.IsNullOrWhiteSpace(hostOptions.LegacyScenarioDirectory))
            {
                throw new InvalidOperationException(
                    "Bess:LegacyScenarioDirectory is required when Bess:ShadowModeEnabled is true.");
            }

            services.AddLegacyDeyeScenarioShadowImport(hostOptions.LegacyScenarioDirectory);
            services.AddSingleton(new ShadowDeyeProjectionOptions());
            if (!hostOptions.Planning.Enabled)
            { services.AddSingleton<IOrchestrationModule, ScheduleShadowPlanProjectionModule>(); }
            ConfigureShadowScheduler(services, hostOptions);
        }
        else if (hostOptions.ShadowSchedulerEnabled)
        {
            throw new InvalidOperationException(
                "Bess:ShadowSchedulerEnabled requires Bess:ShadowModeEnabled=true.");
        }

        ValidateActivationCutover(hostOptions);

        ConfigureScheduleSolver(services, hostOptions);
        ConfigureMpcBackend(services, hostOptions);
    }

    private static void ValidateActivationCutover(BessHostOptions hostOptions)
    {
        if (!hostOptions.ActivationCutoverEnabled)
        {
            if (hostOptions.ActivationPilotEnabled)
            {
                throw new InvalidOperationException(
                    "Bess:ActivationPilotEnabled requires Bess:ActivationCutoverEnabled=true.");
            }

            return;
        }

        if (!hostOptions.ShadowModeEnabled)
        {
            throw new InvalidOperationException(
                "Bess:ActivationCutoverEnabled requires Bess:ShadowModeEnabled=true.");
        }

        if (!hostOptions.ActivationPilotEnabled)
        {
            throw new InvalidOperationException(
                "Bess:ActivationCutoverEnabled requires Bess:ActivationPilotEnabled=true.");
        }

        if (string.IsNullOrWhiteSpace(hostOptions.PersistenceConnectionString))
        {
            throw new InvalidOperationException(
                "Bess:PersistenceConnectionString is required when Bess:ActivationCutoverEnabled is true.");
        }
    }

    private static void ConfigureShadowScheduler(
        IServiceCollection services,
        BessHostOptions hostOptions)
    {
        if (!hostOptions.ShadowSchedulerEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(hostOptions.ShadowSiteId)
            || hostOptions.ShadowTriggerHourLocal is null
            || hostOptions.ShadowTriggerMinuteLocal is null)
        {
            throw new InvalidOperationException(
                "Bess:ShadowSiteId, ShadowTriggerHourLocal and ShadowTriggerMinuteLocal "
                + "are required when Bess:ShadowSchedulerEnabled is true.");
        }

        if (string.IsNullOrWhiteSpace(hostOptions.PersistenceConnectionString)
            && !hostOptions.ShadowSchedulerAllowInMemory)
        {
            throw new InvalidOperationException(
                "Bess:PersistenceConnectionString is required for the shadow scheduler. "
                + "Bess:ShadowSchedulerAllowInMemory=true is restricted to tests and local evaluation.");
        }

        var options = new ShadowDayAheadSchedulerOptions(
            hostOptions.ShadowSiteId,
            hostOptions.ShadowTriggerHourLocal.Value,
            hostOptions.ShadowTriggerMinuteLocal.Value,
            hostOptions.ShadowPollIntervalSeconds).EnsureValid();
        services.AddSingleton(options);
        services.AddSingleton<ShadowDayAheadScheduler>();
        services.AddHostedService<ShadowDayAheadSchedulerHostedService>();
    }

    private static void ConfigureIoAdapters(
        IServiceCollection services,
        BessHostOptions hostOptions,
        BessRuntimeConfiguration runtimeConfig,
        ConfigurationManager configuration)
    {
        if (string.Equals(hostOptions.TelemetrySource, "deye_cloud", StringComparison.OrdinalIgnoreCase))
        {
            EnsureDeyeCloudAssetCardinality(runtimeConfig);
            services.AddDeyeCloudTelemetry(configuration, runtimeConfig.SingleAsset.AssetId);
            services.AddSingleton<IBatteryCommandSink, NoOpBatteryCommandSink>();
            return;
        }

        var family = SelectIoAdapterFamily(hostOptions, runtimeConfig);
        EnsureIoAdapterAssetCardinality(family, runtimeConfig);
        switch (family)
        {
            case IoAdapterTriage.Family.Modbus:
                services.AddBessModbus(
                    runtimeConfig.ModbusMapping!,
                    ModbusAdapterOptions.Defaults(hostOptions.ModbusHost!, hostOptions.ModbusPort, runtimeConfig.SingleAsset.AssetId));
                break;
            case IoAdapterTriage.Family.Mqtt:
                services.AddBessMqtt(
                    runtimeConfig.MqttMapping!,
                    BuildMqttAdapterOptions(hostOptions, runtimeConfig.SingleAsset.AssetId));
                break;
            case IoAdapterTriage.Family.OpcUa:
                services.AddBessOpcUa(
                    runtimeConfig.OpcUaMapping!,
                    BuildOpcUaAdapterOptions(hostOptions));
                break;
            case IoAdapterTriage.Family.None:
            default:
                services.AddSingleton<IBatteryTelemetrySource, NoOpBatteryTelemetrySource>();
                services.AddSingleton<IBatteryCommandSink, NoOpBatteryCommandSink>();
                break;
        }
    }

    private static void ConfigurePriceSeriesSource(IServiceCollection services, BessHostOptions hostOptions)
    {
        if (string.IsNullOrWhiteSpace(hostOptions.PriceSeriesSource))
        {
            return;
        }

        if (string.Equals(hostOptions.PriceSeriesSource, "entso-e", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostOptions.PriceSeriesSource, "entsoe", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(hostOptions.EntsoeApiToken)
                || string.IsNullOrWhiteSpace(hostOptions.EntsoeDomainCode))
            {
                throw new InvalidOperationException(
                    "Bess:PriceSeriesSource='entso-e' requires Bess:EntsoeApiToken and Bess:EntsoeDomainCode.");
            }

            var options = new EntsoePriceSeriesOptions
            {
                BaseUrl = hostOptions.EntsoeApiBaseUrl ?? new Uri("https://web-api.tp.entsoe.eu/api"),
                SecurityToken = hostOptions.EntsoeApiToken!,
                DomainCode = hostOptions.EntsoeDomainCode!,
                RequestTimeout = hostOptions.EntsoeRequestTimeout ?? TimeSpan.FromSeconds(60),
            };
            services.AddSingleton(options);
            services.AddSingleton<HttpClient>();
            services.AddSingleton<IPriceSeriesSource, EntsoePriceSeriesSource>();
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Bess:PriceSeriesSource '{hostOptions.PriceSeriesSource}'. Supported values: entso-e.");
    }

    private static void ConfigureSiteTelemetry(
        IServiceCollection services,
        BessHostOptions hostOptions,
        BessRuntimeConfiguration runtimeConfig,
        ConfigurationManager configuration)
    {
        if (string.IsNullOrWhiteSpace(hostOptions.SiteTelemetrySource))
        {
            return;
        }

        if (string.Equals(hostOptions.SiteTelemetrySource, "fusionsolar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostOptions.SiteTelemetrySource, "fusion_solar", StringComparison.OrdinalIgnoreCase))
        {
            EnsureSiteTelemetryAssetCardinality(runtimeConfig);
            services.AddFusionSolarSiteTelemetry(configuration, runtimeConfig.SingleAsset.AssetId);
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Bess:SiteTelemetrySource '{hostOptions.SiteTelemetrySource}'. Supported values: fusionsolar.");
    }

    private static void ConfigureSiteConsumption(
        IServiceCollection services,
        BessHostOptions hostOptions,
        BessRuntimeConfiguration runtimeConfig,
        ConfigurationManager configuration)
    {
        if (string.IsNullOrWhiteSpace(hostOptions.ConsumptionSource))
        {
            return;
        }

        if (string.Equals(hostOptions.ConsumptionSource, "askue", StringComparison.OrdinalIgnoreCase))
        {
            if (configuration.GetSection("Askue:Accounts").Exists())
            {
                services.AddAskueSiteConsumption(configuration);
                return;
            }
            EnsureSiteTelemetryAssetCardinality(runtimeConfig);
            services.AddAskueSiteConsumption(configuration, runtimeConfig.SingleAsset.AssetId);
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Bess:ConsumptionSource '{hostOptions.ConsumptionSource}'. Supported values: askue.");
    }

    private static void EnsureDeyeCloudAssetCardinality(BessRuntimeConfiguration runtimeConfig)
    {
        if (runtimeConfig.Assets.Count == 1)
        {
            return;
        }

        throw new InvalidOperationException(
            "Bess:TelemetrySource='deye_cloud' requires exactly one configured asset "
            + "unless DeyeCloud:AssetId routing is extended for multi-asset use.");
    }

    private static void EnsureSiteTelemetryAssetCardinality(BessRuntimeConfiguration runtimeConfig)
    {
        if (runtimeConfig.Assets.Count == 1)
        {
            return;
        }

        throw new InvalidOperationException(
            "Bess:SiteTelemetrySource requires exactly one configured asset "
            + "unless the site telemetry adapter is configured with explicit per-station asset routing.");
    }

    private static void EnsureIoAdapterAssetCardinality(
        IoAdapterTriage.Family family,
        BessRuntimeConfiguration runtimeConfig)
    {
        if (family == IoAdapterTriage.Family.None || runtimeConfig.Assets.Count == 1)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Configured IO adapter family '{family}' requires exactly one asset, "
            + $"but Bess:AssetConfigPath contains {runtimeConfig.Assets.Count} assets. "
            + "Multi-asset configs are currently supported only with NoOp IO until "
            + "per-asset adapter and command-sink mapping is configured.");
    }

    private static IoAdapterTriage.Family SelectIoAdapterFamily(
        BessHostOptions hostOptions,
        BessRuntimeConfiguration runtimeConfig)
    {
        var modbusConfigured = runtimeConfig.ModbusMapping is not null
            && !string.IsNullOrWhiteSpace(hostOptions.ModbusHost)
            && hostOptions.ModbusPort > 0;
        var mqttConfigured = runtimeConfig.MqttMapping is not null
            && !string.IsNullOrWhiteSpace(hostOptions.MqttBrokerHost)
            && hostOptions.MqttBrokerPort > 0
            && !string.IsNullOrWhiteSpace(hostOptions.MqttClientId);
        var opcUaConfigured = runtimeConfig.OpcUaMapping is not null
            && hostOptions.OpcUaEndpointUrl is not null;
        return IoAdapterTriage.SelectConfiguredFamily(
            modbusConfigured, mqttConfigured, opcUaConfigured);
    }

    private static void ConfigureApp(
        WebApplication app,
        BessHostOptions hostOptions,
        BessRuntimeConfiguration runtimeConfig)
    {
        MigratePersistence(app, hostOptions);
        SeedRuntimeState(app, runtimeConfig);
        _ = app.Services.GetRequiredService<ApiTokenRegistry>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOperatorUiStaticShell();
        app.MapOpenApi();
        app.MapBatteryEms();
        app.MapSiteTelemetryStatus();
        app.MapChpTelemetry();
        app.MapSiteData();
        app.MapSitePvProfiles();
        app.MapSiteFinancialPlans();
        app.MapEmsPlanning(hostOptions.Planning);
        app.MapDeyeDayAuthorization(hostOptions.Planning);
        app.MapSolarForecasts();
        app.MapAgentEndpoints(
            hostOptions.ShadowModeEnabled,
            hostOptions.ActivationCutoverEnabled,
            hostOptions.ActivationPilotEnabled);
        app.MapMetrics();
    }

    private static void MigratePersistence(WebApplication app, BessHostOptions hostOptions)
    {
        if (!string.IsNullOrWhiteSpace(hostOptions.PersistenceConnectionString))
        {
            var migrator = app.Services.GetRequiredService<BessDbMigrator>();
            migrator.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static void SeedRuntimeState(WebApplication app, BessRuntimeConfiguration runtimeConfig)
    {
        var assets = app.Services.GetRequiredService<IBatteryAssetRegistry>();
        BessConfigurationBootstrap.SeedAssetRegistry(assets, runtimeConfig.Assets);
        if (runtimeConfig.Schedule is not null)
        {
            var schedules = app.Services.GetRequiredService<IScheduleRepository>();
            BessConfigurationBootstrap.SeedScheduleRepository(schedules, runtimeConfig.Schedule);
        }
    }

    private static void ConfigureScheduleSolver(
        IServiceCollection services,
        BessHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hostOptions);

        var options = hostOptions.ScheduleSolver;
        var backend = string.IsNullOrWhiteSpace(options.Backend)
            ? "noop"
            : options.Backend.Trim();
        if (string.Equals(backend, "noop", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (string.Equals(backend, "or_tools", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "ortools", StringComparison.OrdinalIgnoreCase))
        {
            services.AddBessScheduleSolver(solver =>
            {
                if (options.TimeLimitSeconds is { } seconds)
                {
                    solver.TimeLimit = TimeSpan.FromSeconds(seconds);
                }
                solver.GapTolerance = options.GapTolerance;
                solver.InitialSocPercent = options.InitialSocPercent;
                if (options.DegradationCostPerKwhThroughput is { } degradationRate)
                {
                    solver.DegradationCost = new DegradationCostOptions
                    {
                        EurPerKwhThroughput = degradationRate,
                        NominalCRate = options.DegradationNominalCRate,
                        PiecewiseSegments = options.DegradationPiecewiseSegments ?? 8,
                    };
                }
            });
            return;
        }
        if (string.Equals(backend, "optimization_core", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "optimizationcore", StringComparison.OrdinalIgnoreCase))
        {
            // RM-M5-01-A (ADR 0005): gRPC-Sidecar-Adapter. SidecarEndpoint
            // ist Pflicht; alle übrigen Slots übernehmen Adapter-Defaults
            // wenn nicht gesetzt. EnsureValid läuft am ScheduleOptimizer-
            // Konstruktor und failed bei Production+plaintext-Endpoint.
            if (hostOptions.OptimizationCoreSidecarEndpoint is null)
            {
                throw new InvalidOperationException(
                    "Bess:ScheduleSolver:Backend='optimization_core' requires "
                    + "Bess:OptimizationCoreSidecarEndpoint to be set (e.g. "
                    + "`unix:///var/run/bess-ems/optimization-core.sock` "
                    + "for the Loopback-Default or `https://...` for the "
                    + "Cross-Host-Production-Pfad gemäß ADR 0005 §4).");
            }
            // RM-M5-01-C Korrektur-Pass (plan-RM-M5 §Fallback-Matrix):
            // wenn der Operator einen lokalen Fallback-Optimizer setzt,
            // registrieren wir den entsprechenden Adapter als
            // `IFallbackScheduleOptimizer` BEFORE `AddBessOptimizationCore`,
            // damit die Factory im OptimizationCoreScheduleOptimizer ihn
            // via `GetService<IFallbackScheduleOptimizer>()` auflöst.
            ConfigureOptimizationCoreFallback(services, hostOptions, options);
            services.AddBessOptimizationCore(BuildOptimizationCoreOptions(hostOptions));
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Bess:ScheduleSolver:Backend '{options.Backend}'. "
            + "Supported values: noop, or_tools, optimization_core.");
    }

    private static void ConfigureMpcBackend(
        IServiceCollection services,
        BessHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hostOptions);

        if (string.IsNullOrWhiteSpace(hostOptions.MpcBackend))
        {
            return;
        }

        var backend = hostOptions.MpcBackend.Trim();
        if (string.Equals(backend, "optimization_core", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backend, "bi_modal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"mpc-backend-not-implemented: Bess:MpcBackend='{hostOptions.MpcBackend}' is reserved for F-M5-12.");
        }
        if (string.Equals(backend, "local_osqp", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureMpcClock(services, hostOptions);
            services.AddBessLocalOsqpMpcSolver();
            ValidateMpcProductionGates(services, hostOptions);
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Bess:MpcBackend '{hostOptions.MpcBackend}'. Supported values: local_osqp.");
    }

    private static void ConfigureMpcClock(IServiceCollection services, BessHostOptions hostOptions)
    {
        if (string.IsNullOrWhiteSpace(hostOptions.MpcClock))
        {
            return;
        }

        var clock = hostOptions.MpcClock.Trim();
        if (string.Equals(clock, "monotonic_anchored", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IClock, MonotonicAnchoredClock>();
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Bess:MpcClock '{hostOptions.MpcClock}'. Supported values: monotonic_anchored.");
    }

    private static void ValidateMpcProductionGates(
        IServiceCollection services,
        BessHostOptions hostOptions)
    {
        if (hostOptions.MpcRuntimeProfile != MpcRuntimeProfile.Production)
        {
            return;
        }

        if (!services.Any(d => d.ServiceType == typeof(IFallbackMpcOptimizer)))
        {
            throw new InvalidOperationException(
                "mpc-production-without-fallback-pathway: Production MPC requires IFallbackMpcOptimizer.");
        }
        if (!services.Any(IsMonotonicAnchoredClockRegistration))
        {
            throw new InvalidOperationException(
                "mpc-production-without-monotonic-clock: Production MPC requires Bess:MpcClock='monotonic_anchored'.");
        }
    }

    private static bool IsMonotonicAnchoredClockRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IClock)
        && (descriptor.ImplementationType == typeof(MonotonicAnchoredClock)
            || descriptor.ImplementationInstance is MonotonicAnchoredClock);

    // RM-M5-01-C Korrektur-Pass: registriert den lokalen Fallback-
    // Optimizer (plan-RM-M5 §Fallback-Matrix) wenn der Operator
    // `OptimizationCoreFallbackBackend` setzt. Aktuell unterstützt:
    // `or_tools` ⇒ AddBessScheduleSolverAsFallback. `null` oder leer
    // ⇒ kein Fallback (no_valid_plan + Safe-Stop bei Sidecar-Failure).
    private static void ConfigureOptimizationCoreFallback(
        IServiceCollection services,
        BessHostOptions hostOptions,
        BessScheduleSolverOptions solverOptions)
    {
        var fallbackBackend = hostOptions.OptimizationCoreFallbackBackend;
        if (string.IsNullOrWhiteSpace(fallbackBackend))
        {
            return;
        }
        var trimmed = fallbackBackend.Trim();
        if (string.Equals(trimmed, "or_tools", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "ortools", StringComparison.OrdinalIgnoreCase))
        {
            services.AddBessScheduleSolverAsFallback(solver =>
            {
                if (solverOptions.TimeLimitSeconds is { } seconds)
                {
                    solver.TimeLimit = TimeSpan.FromSeconds(seconds);
                }
                solver.GapTolerance = solverOptions.GapTolerance;
                solver.InitialSocPercent = solverOptions.InitialSocPercent;
                if (solverOptions.DegradationCostPerKwhThroughput is { } degradationRate)
                {
                    solver.DegradationCost = new DegradationCostOptions
                    {
                        EurPerKwhThroughput = degradationRate,
                        NominalCRate = solverOptions.DegradationNominalCRate,
                        PiecewiseSegments = solverOptions.DegradationPiecewiseSegments ?? 8,
                    };
                }
            });
            return;
        }
        throw new InvalidOperationException(
            $"Unsupported Bess:OptimizationCoreFallbackBackend '{fallbackBackend}'. "
            + "Supported values: or_tools (or empty for no fallback ⇒ "
            + "no_valid_plan + Safe-Stop on Sidecar-Failure per plan-RM-M5 "
            + "§Fallback-Matrix).");
    }

    // RM-M5-01-A Helper: baut `OptimizationCoreOptions` aus den
    // BessHostOptions-Slots. Operator-Overrides — falls gesetzt —
    // überschreiben Adapter-Defaults; sonst gilt
    // Production+SignAndEncrypt-Equivalent für gRPC (RuntimeProfile=
    // Production, ConnectTimeout=10s, RequestDeadline=60s,
    // ExpectedContractVersion=1.0.0).
    private static OptimizationCoreOptions BuildOptimizationCoreOptions(
        BessHostOptions hostOptions)
    {
        var defaults = new OptimizationCoreOptions
        {
            SidecarEndpoint = hostOptions.OptimizationCoreSidecarEndpoint!,
        };
        return defaults with
        {
            RuntimeProfile = hostOptions.OptimizationCoreRuntimeProfile
                ?? defaults.RuntimeProfile,
            ExpectedContractVersion = string.IsNullOrWhiteSpace(
                hostOptions.OptimizationCoreExpectedContractVersion)
                    ? defaults.ExpectedContractVersion
                    : hostOptions.OptimizationCoreExpectedContractVersion!,
            ClientCertificatePath = string.IsNullOrWhiteSpace(
                hostOptions.OptimizationCoreClientCertificatePath)
                    ? defaults.ClientCertificatePath
                    : hostOptions.OptimizationCoreClientCertificatePath!,
            TrustedServerCertificatesPath = string.IsNullOrWhiteSpace(
                hostOptions.OptimizationCoreTrustedServerCertificatesPath)
                    ? defaults.TrustedServerCertificatesPath
                    : hostOptions.OptimizationCoreTrustedServerCertificatesPath!,
            BearerTokenPath = string.IsNullOrWhiteSpace(
                hostOptions.OptimizationCoreBearerTokenPath)
                    ? defaults.BearerTokenPath
                    : hostOptions.OptimizationCoreBearerTokenPath!,
            MaxFallbackScheduleAge = hostOptions.OptimizationCoreMaxFallbackScheduleAge
                ?? defaults.MaxFallbackScheduleAge,
        };
    }

    private static MqttAdapterOptions BuildMqttAdapterOptions(
        BessHostOptions hostOptions,
        string assetId)
    {
        var defaults = MqttAdapterOptions.Defaults(
            hostOptions.MqttBrokerHost!,
            hostOptions.MqttBrokerPort,
            hostOptions.MqttClientId!,
            assetId);
        return defaults with
        {
            RuntimeProfile = hostOptions.MqttRuntimeProfile ?? defaults.RuntimeProfile,
            Tls = new MqttTlsOptions(
                Enabled: hostOptions.MqttTlsEnabled,
                TrustedCaCertificatePath: hostOptions.MqttTlsTrustedCaCertificatePath,
                ClientCertificatePath: hostOptions.MqttTlsClientCertificatePath,
                ClientCertificatePassword: hostOptions.MqttTlsClientCertificatePassword,
                ClientCertificatePasswordPath: hostOptions.MqttTlsClientCertificatePasswordPath),
            Credentials = new MqttCredentialOptions(
                Username: hostOptions.MqttUsername,
                Password: hostOptions.MqttPassword,
                PasswordPath: hostOptions.MqttPasswordPath),
            QoS = BuildMqttQosOptions(hostOptions, defaults.QoSOrDefault),
            AllowPlaintext = hostOptions.MqttAllowPlaintext,
            AllowPlaintextReason = hostOptions.MqttAllowPlaintextReason,
            AllowExactlyOnce = hostOptions.MqttAllowExactlyOnce,
            AllowExactlyOnceReason = hostOptions.MqttAllowExactlyOnceReason,
        };
    }

    private static MqttQosOptions BuildMqttQosOptions(
        BessHostOptions hostOptions,
        MqttQosOptions defaults) => new(
            CommandPublish: hostOptions.MqttCommandPublishQos ?? defaults.CommandPublish,
            CommandAckSubscribe: hostOptions.MqttCommandAckSubscribeQos ?? defaults.CommandAckSubscribe,
            TelemetrySubscribe: hostOptions.MqttTelemetrySubscribeQos ?? defaults.TelemetrySubscribe);

    // M4-05: BessHostOptions kennt jetzt RuntimeProfile/SecurityMode/
    // SecurityPolicy + Cert-Subject/TrustedStore-Slots. Helper baut die
    // Adapter-Options, indem die Operator-Overrides — falls gesetzt —
    // die Adapter-Defaults überschreiben. Null lässt den Default-Wert
    // aus `OpcUaAdapterOptions` greifen (Production + SignAndEncrypt +
    // Basic256Sha256). Die finale `EnsureValid`-Validierung läuft auf
    // der Source/Sink-Konstruktor-Linie (D-04 + M4-05 D-02).
    private static OpcUaAdapterOptions BuildOpcUaAdapterOptions(BessHostOptions hostOptions)
    {
        var defaults = new OpcUaAdapterOptions
        {
            EndpointUrl = hostOptions.OpcUaEndpointUrl!,
        };
        return defaults with
        {
            SessionName = string.IsNullOrWhiteSpace(hostOptions.OpcUaSessionName)
                ? defaults.SessionName
                : hostOptions.OpcUaSessionName!,
            RuntimeProfile = hostOptions.OpcUaRuntimeProfile ?? defaults.RuntimeProfile,
            SecurityMode = hostOptions.OpcUaSecurityMode ?? defaults.SecurityMode,
            SecurityPolicy = string.IsNullOrWhiteSpace(hostOptions.OpcUaSecurityPolicy)
                ? defaults.SecurityPolicy
                : hostOptions.OpcUaSecurityPolicy!,
            ApplicationCertificateSubject = string.IsNullOrWhiteSpace(
                hostOptions.OpcUaApplicationCertificateSubject)
                    ? defaults.ApplicationCertificateSubject
                    : hostOptions.OpcUaApplicationCertificateSubject!,
            TrustedServerCertificatesPath = string.IsNullOrWhiteSpace(
                hostOptions.OpcUaTrustedServerCertificatesPath)
                    ? defaults.TrustedServerCertificatesPath
                    : hostOptions.OpcUaTrustedServerCertificatesPath!,
            AllowUnsecured = hostOptions.OpcUaAllowUnsecured,
            AllowUnsecuredReason = hostOptions.OpcUaAllowUnsecuredReason,
        };
    }
}
