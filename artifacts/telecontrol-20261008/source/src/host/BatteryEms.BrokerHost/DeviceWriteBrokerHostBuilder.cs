using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using BatteryEms.Adapters.DeyeCloud;
using BatteryEms.Adapters.Persistence;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BatteryEms.BrokerHost;

public static class DeviceWriteBrokerHostBuilder
{
    // Test seam only; production uses configuration supplied to the distinct
    // broker process, never the operational EMS host configuration.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1506",
        Justification = "This is the separate broker composition root; it binds authentication, database and fail-closed driver in one auditable place.")]
    public static WebApplication BuildApp(string[] args, Action<WebApplicationBuilder>? configure = null,
        Action<IServiceCollection>? configureTestServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2048);
        configure?.Invoke(builder);
        var options = builder.Configuration.GetSection("Broker").Get<BrokerServerOptions>() ?? new();
        var appEnabled = options.Enabled;
        if (appEnabled)
        {
            options.EnsureValid();
            builder.WebHost.UseUrls(options.ListenUrl!.AbsoluteUri);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<IClock>(_ => new BrokerUtcClock());
            builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.PersistenceConnectionString));
            builder.Services.AddSingleton(sp => new BessDbMigrator(sp.GetRequiredService<NpgsqlDataSource>(),
                options.PersistenceConnectionString, sp.GetRequiredService<ILogger<BessDbMigrator>>()));
            builder.Services.AddSingleton(sp => new DapperDeviceWriteBrokerAttemptStore(
                sp.GetRequiredService<NpgsqlDataSource>(), options.DayAuthorizationsEnabled));
            builder.Services.AddSingleton<IDeviceWriteBrokerAttemptStore>(sp => sp.GetRequiredService<DapperDeviceWriteBrokerAttemptStore>());
            builder.Services.AddSingleton<IDeviceWriteBrokerMutationGate>(sp => sp.GetRequiredService<DapperDeviceWriteBrokerAttemptStore>());
            builder.Services.AddSingleton<IDeviceWriteBrokerPlanResolver>(sp => new DapperDeviceWriteBrokerPlanResolver(
                sp.GetRequiredService<NpgsqlDataSource>(), options.DayAuthorizationsEnabled));
            builder.Services.AddSingleton<IDeviceWriteBrokerDriver, FailClosedDeviceWriteBrokerDriver>();
            if (options.Deye.Enabled && options.Deye.WriteEnabled)
            {
                var device = options.Deye.Device();
                var auth = options.Deye.Token();
                var vendorBase = options.Deye.BaseAddress!;
                builder.Services.AddSingleton(_ => DeyeBrokerTokenProvider.CreateAuthClient(vendorBase));
                builder.Services.AddSingleton(sp => new DeyeBrokerTokenProvider(sp.GetRequiredService<HttpClient>(), auth, sp.GetRequiredService<IClock>()));
                builder.Services.AddSingleton<IBrokerDriverSessionFactory>(sp => new DeyeBrokerDriverSessionFactory(
                    vendorBase, device, sp.GetRequiredService<DeyeBrokerTokenProvider>(),
                    sp.GetRequiredService<IClock>(), sp.GetRequiredService<IDeviceWriteBrokerMutationGate>()));
            }
            else
            {
                builder.Services.AddSingleton<IBrokerDriverSessionFactory>(sp =>
                    new DefaultBrokerDriverSessionFactory(sp.GetRequiredService<IDeviceWriteBrokerDriver>()));
            }
        }
        configureTestServices?.Invoke(builder.Services);
        var app = builder.Build();
        if (!appEnabled)
        {
            app.MapGet("/healthz", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return app;
        }

        app.Services.GetRequiredService<BessDbMigrator>().MigrateAsync(CancellationToken.None)
            .GetAwaiter().GetResult();
        var credentials = options.Clients.Select(client => new BrokerCredential(client)).ToArray();
        app.MapGet("/livez", () => Results.Ok(new { status = "alive" }));
        // An authenticated shell with no device driver is not write-ready.
        app.MapGet("/healthz", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapPost("/v1/device-writes", async (HttpContext context,
            BrokerWriteRequest body, IBrokerDriverSessionFactory sessions,
            IDeviceWriteBrokerAttemptStore store, IDeviceWriteBrokerPlanResolver resolver,
            IClock clock, CancellationToken token) =>
        {
            if (!context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var caller = Authenticate(context.Request.Headers.Authorization.ToString(), credentials);
            if (caller is null)
            {
                return Results.Unauthorized();
            }
            if (!caller.Sites.Contains(body.SiteId, StringComparer.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (!sessions.SupportsSite(body.SiteId))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            try
            {
                var request = new DeviceWriteBrokerBeginRequest(body.AttemptId, body.SiteId,
                    body.DeliveryDate, body.WindowId, body.PayloadHash, caller.Role, caller.OwnerId,
                    body.SafetyRevision, body.FencingToken, body.ActivationClaimId, clock.UtcNow).EnsureValid();
                BrokerDriverSession? prepared;
                try { prepared = await sessions.OpenAsync(token).ConfigureAwait(false); }
                catch (InvalidOperationException)
                { return Results.Json(new { code = "device-write-broker-auth-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { return Results.Json(new { code = "device-write-broker-auth-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable); }
                using var session = prepared;
                if (session is null) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
                var executor = new DeviceWriteBrokerExecutionUseCase(store, resolver, session.Driver, clock);
                var result = await executor.ExecuteAsync(request, token).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { code = "device-write-broker-request-invalid" });
            }
        });
        return app;
    }

    private static BrokerCredential? Authenticate(string header, IReadOnlyList<BrokerCredential> credentials)
    {
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal) || header.Length > 263)
        {
            return null;
        }
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(header[prefix.Length..]));
        BrokerCredential? match = null;
        foreach (var credential in credentials)
        {
            if (CryptographicOperations.FixedTimeEquals(candidate, credential.TokenDigest))
            {
                match = credential;
            }
        }
        return match;
    }

    private sealed class BrokerCredential
    {
        public BrokerCredential(BrokerClientOptions options)
        {
            Role = options.Role;
            OwnerId = options.OwnerId;
            Sites = options.Sites.ToArray();
            TokenDigest = SHA256.HashData(Encoding.UTF8.GetBytes(options.Token));
        }

        public ActivationWriterAuthority Role { get; }
        public string OwnerId { get; }
        public IReadOnlyList<string> Sites { get; }
        public byte[] TokenDigest { get; }
    }
}

public sealed class BrokerServerOptions
{
    public bool DayAuthorizationsEnabled { get; set; }
    public bool Enabled { get; set; }
    public Uri? ListenUrl { get; set; }
    public string PersistenceConnectionString { get; set; } = string.Empty;
    public Collection<BrokerClientOptions> Clients { get; } = [];
    public BrokerDeyeOptions Deye { get; set; } = new();

    public BrokerServerOptions EnsureValid()
    {
        Deye.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(PersistenceConnectionString);
        if (ListenUrl is not { IsAbsoluteUri: true } listen
            || listen.Scheme != Uri.UriSchemeHttps
            || listen.AbsolutePath != "/" || !string.IsNullOrEmpty(listen.Query)
            || !string.IsNullOrEmpty(listen.Fragment) || !string.IsNullOrEmpty(listen.UserInfo)
            || listen.Host is "*" or "+" or "0.0.0.0" or "::")
        {
            throw new ArgumentException("An explicit non-wildcard HTTPS broker listen URL is required.");
        }
        if (Clients.Count != 2
            || Clients.Count(client => client.Role == ActivationWriterAuthority.LegacyRunner) != 1
            || Clients.Count(client => client.Role == ActivationWriterAuthority.ProductAgent) != 1
            || Clients.Any(client => string.IsNullOrWhiteSpace(client.OwnerId)
                || string.IsNullOrWhiteSpace(client.Token) || client.Token.Length < 32
                || client.Sites.Count == 0 || client.Sites.Any(string.IsNullOrWhiteSpace))
            || Clients.Select(client => client.Token).Distinct(StringComparer.Ordinal).Count() != 2
            || Clients.Select(client => client.OwnerId).Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new ArgumentException("Broker clients need distinct roles, owners, secrets and explicit site allowlists.");
        }
        if (Deye.Enabled && Clients.SelectMany(client => client.Sites).Any(site => site != Deye.SiteId))
        { throw new ArgumentException("This broker driver requires client allowlists bound to its single configured site."); }
        return this;
    }
}

public sealed class BrokerClientOptions
{
    public ActivationWriterAuthority Role { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public Collection<string> Sites { get; } = [];
}

// No raw vendor URL, serial, body, owner, authority or client clock is exposed.
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerWriteRequest(Guid AttemptId, string SiteId, DateOnly DeliveryDate,
    string WindowId, string PayloadHash, long SafetyRevision, long FencingToken,
    Guid? ActivationClaimId);

internal sealed class BrokerUtcClock : IClock
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}
