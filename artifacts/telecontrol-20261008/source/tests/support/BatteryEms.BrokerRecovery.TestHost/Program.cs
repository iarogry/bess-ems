using System.Text.Json;
using BatteryEms.Adapters.Persistence;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using BatteryEms.BrokerHost;
using Npgsql;

namespace BatteryEms.BrokerRecovery.TestHost;

// Test-only executable. Production hosts never load this composition.
internal static class Program
{
    private const string Site = "synthetic-recovery-site";
    private const string Owner = "synthetic-legacy-owner";
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 20, 55, 25, TimeSpan.Zero);
    private static readonly string[] Times = ["00:00", "04:00", "08:00", "12:00", "16:00", "20:00"];

    public static async Task Main()
    {
        var connection = Required("RECOVERY_CONNECTION");
        var database = new NpgsqlConnectionStringBuilder(connection);
        if (database.Host != "127.0.0.1" || database.Database != "bess_recovery_owned")
        {
            throw new InvalidOperationException("Recovery probe requires its owned loopback database.");
        }
        var listen = new Uri(Required("RECOVERY_LISTEN"));
        var vendor = new Uri(Required("RECOVERY_VENDOR"));
        if (!listen.IsLoopback || listen.Scheme != "https" || !vendor.IsLoopback || vendor.Scheme != "http")
        {
            throw new InvalidOperationException("Synthetic recovery transports must remain loopback-only.");
        }
        var evidence = Required("RECOVERY_EVIDENCE");
        var phase = Required("RECOVERY_PHASE");
        var plan = new ShadowPlanSnapshot(Site, new DateOnly(2026, 9, 23), true, [],
            Enumerable.Range(1, 4).Select(index => new ShadowTouWindow($"Z{index}",
                Times.Select(time => new ShadowTouInterval(time, true, false, false, 10000, 30, 0)).ToArray())).ToArray());
        var settings = new Dictionary<string, string?>
        {
            ["Broker:Enabled"] = "true",
            ["Broker:ListenUrl"] = listen.AbsoluteUri,
            ["Broker:PersistenceConnectionString"] = connection,
            ["Broker:Deye:Enabled"] = "false",
            ["Broker:Deye:WriteEnabled"] = "false",
            ["Broker:Clients:0:Role"] = "LegacyRunner",
            ["Broker:Clients:0:OwnerId"] = Owner,
            ["Broker:Clients:0:Token"] = "synthetic-legacy-token-not-real-12345678",
            ["Broker:Clients:0:Sites:0"] = Site,
            ["Broker:Clients:1:Role"] = "ProductAgent",
            ["Broker:Clients:1:OwnerId"] = "synthetic-product-owner",
            ["Broker:Clients:1:Token"] = "synthetic-product-token-not-real-12345678",
            ["Broker:Clients:1:Sites:0"] = Site,
        };
        using var app = DeviceWriteBrokerHostBuilder.BuildApp([], builder =>
            builder.Configuration.AddInMemoryCollection(settings), services =>
            {
                services.AddSingleton<IClock>(new FixedClock());
                services.AddSingleton<IDeviceWriteBrokerPlanResolver>(new Resolver(plan));
                services.AddSingleton<IDeviceWriteBrokerDriver>(new CrashDriver(vendor, evidence, phase));
            });
        await SeedAsync(connection, plan, evidence).ConfigureAwait(false);
        await app.RunAsync().ConfigureAwait(false);
    }

    private static async Task SeedAsync(string connection, ShadowPlanSnapshot plan, string evidence)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        var safety = new DapperActivationWriterSafetyStore(source);
        if (await safety.FindStateAsync(Site, CancellationToken.None).ConfigureAwait(false) is null)
        {
            var state = ActivationSafetyState.InitialFailClosed(Site, Now, "synthetic-operator", "owned crash probe") with
            { KillSwitchEngaged = false };
            if (!await safety.CompareExchangeStateAsync(state, null, CancellationToken.None).ConfigureAwait(false))
            { throw new InvalidOperationException("Synthetic safety initialization failed."); }
            var lease = await safety.TryAcquireLeaseAsync(Site, Owner, Now, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            if (!lease.Acquired) { throw new InvalidOperationException("Synthetic lease initialization failed."); }
            var request = new BrokerWriteRequest(Guid.NewGuid(), Site, plan.DeliveryDate, "Z1",
                ActivationPayloadIntegrity.ComputeHash(plan), 1, lease.Lease!.FencingToken, null);
            await File.WriteAllTextAsync(Path.Combine(evidence, "request.json"), JsonSerializer.Serialize(request)).ConfigureAwait(false);
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing test setting {name}.");

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class Resolver(ShadowPlanSnapshot plan) : IDeviceWriteBrokerPlanResolver
    {
        public Task<ShadowPlanSnapshot?> ResolveAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ShadowPlanSnapshot?>(plan);
        }
    }

    private sealed class CrashDriver(Uri vendor, string evidence, string phase) : IDeviceWriteBrokerDriver
    {
        public Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public async Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
        {
            if (phase == "after-send")
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, CheckCertificateRevocationList = true });
                using var response = await client.PostAsync(vendor, null, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            // Admission has committed Initiated. The parent kills this process
            // here before observation can complete, with or without a vendor call.
            await File.WriteAllTextAsync(Path.Combine(evidence, "checkpoint"), phase, cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return DeviceWriteBrokerReadback.Unknown;
        }
    }
}
