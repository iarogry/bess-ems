using System.Net;
using System.Net.Http.Json;
using System.Text;
using BatteryEms.Application.Site;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace BatteryEms.Adapters.Askue.Tests;

public sealed class AskueMultiAccountTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Accounts_use_separate_credentials_and_keep_identical_meter_ids_in_separate_sites()
    {
        var services = Services();
        services.AddAskueSiteConsumption(Config());
        var first = new Handler("user-a:password-a", 10);
        var second = new Handler("user-b:password-b", 20);
        services.AddHttpClient("Askue:a").ConfigurePrimaryHttpMessageHandler(() => first);
        services.AddHttpClient("Askue:b").ConfigurePrimaryHttpMessageHandler(() => second);
        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredKeyedService<AskueSiteConsumptionCollector>("a");
        var b = provider.GetRequiredKeyedService<AskueSiteConsumptionCollector>("b");
        await Task.WhenAll(a.CollectAsync(new DateOnly(2026, 10, 8), CancellationToken.None),
            b.CollectAsync(new DateOnly(2026, 10, 8), CancellationToken.None));
        // Repeated polls also retain the same account credentials and isolated store key.
        await a.CollectAsync(new DateOnly(2026, 10, 8), CancellationToken.None);
        var store = provider.GetRequiredService<ISiteConsumptionStore>();
        var readingsA = await store.QueryAsync(new SiteConsumptionQuery("site-a", At.AddHours(-12), At), CancellationToken.None);
        var readingsB = await store.QueryAsync(new SiteConsumptionQuery("site-b", At.AddHours(-12), At), CancellationToken.None);
        Assert.Equal(10, Assert.Single(readingsA).Apoz);
        Assert.Equal(20, Assert.Single(readingsB).Apoz);
        Assert.Equal("101", readingsA[0].PointId);
        Assert.Equal("101", readingsB[0].PointId);
        Assert.Equal(10, first.RequestCount);
        Assert.Equal(5, second.RequestCount);
    }

    [Fact]
    public async Task Authentication_failure_in_one_worker_does_not_stop_another_account()
    {
        var services = Services();
        services.AddAskueSiteConsumption(Config());
        services.AddHttpClient("Askue:a").ConfigurePrimaryHttpMessageHandler(() => new Handler("user-a:password-a", 10, true));
        services.AddHttpClient("Askue:b").ConfigurePrimaryHttpMessageHandler(() => new Handler("user-b:password-b", 20));
        using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().OfType<AskueConsumptionHostedService>().ToArray();
        Assert.Equal(2, workers.Length);
        try
        {
            await Task.WhenAll(workers.Select(worker => worker.StartAsync(CancellationToken.None)));
            var statuses = provider.GetRequiredService<ISiteConsumptionPollStatusStore>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (statuses.All().Count != 2 || statuses.All().Any(status => status.State == "polling"))
            {
                await Task.Delay(10, timeout.Token);
            }
            var failed = Assert.Single(statuses.All(), status => status.AccountId == "a");
            var healthy = Assert.Single(statuses.All(), status => status.AccountId == "b");
            Assert.Equal("authentication_failed", failed.ErrorCode);
            Assert.Null(failed.LastSuccessAt);
            Assert.Equal("ok", healthy.State);
            Assert.Equal(1, healthy.LastImportedReadings);
            Assert.Equal(At, healthy.LastSuccessAt);
        }
        finally { await Task.WhenAll(workers.Select(worker => worker.StopAsync(CancellationToken.None))); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("site-a")]
    public void Named_accounts_require_explicit_unique_site_ids(string siteB)
    {
        var services = Services();
        Assert.Throws<OptionsValidationException>(() => services.AddAskueSiteConsumption(Config(siteB), "fallback-battery"));
    }

    [Fact]
    public void Disabled_accounts_need_no_credentials_and_legacy_configuration_still_works()
    {
        var values = Values();
        values["Askue:Accounts:b:Enabled"] = "false";
        values.Remove("Askue:Accounts:b:Password");
        var services = Services();
        services.AddAskueSiteConsumption(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IHostedService>());
        Assert.Null(provider.GetKeyedService<AskueSiteConsumptionCollector>("b"));
        var legacy = Services();
        legacy.AddAskueSiteConsumption(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Askue:Username"] = "old-user", ["Askue:Password"] = "old-password" }).Build(), "old-site");
        using var old = legacy.BuildServiceProvider();
        Assert.Equal("old-site", old.GetRequiredService<IOptions<AskueOptions>>().Value.SiteId);
        Assert.Single(old.GetServices<IHostedService>());
        Assert.NotNull(old.GetRequiredService<AskueSiteConsumptionCollector>());
    }

    [Fact]
    public async Task Missing_requested_meter_cannot_silently_report_success_for_wrong_account()
    {
        var values = Values();
        values["Askue:Accounts:a:PointIds"] = "999";
        var services = Services();
        services.AddAskueSiteConsumption(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddHttpClient("Askue:a").ConfigurePrimaryHttpMessageHandler(() => new Handler("user-a:password-a", 10));
        using var provider = services.BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredKeyedService<AskueSiteConsumptionCollector>("a")
            .CollectAsync(new DateOnly(2026, 10, 8), CancellationToken.None));
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISiteConsumptionStore, InMemorySiteConsumptionStore>();
        services.AddSingleton<TimeProvider>(new FixedTime());
        return services;
    }
    private static IConfiguration Config(string siteB = "site-b")
    {
        var values = Values(); values["Askue:Accounts:b:SiteId"] = siteB;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private static Dictionary<string, string?> Values()
    {
        var values = new Dictionary<string, string?>();
        foreach (var id in new[] { "a", "b" })
        {
            var prefix = "Askue:Accounts:" + id + ":";
            values[prefix + "SiteId"] = "site-" + id;
            values[prefix + "Username"] = "user-" + id;
            values[prefix + "Password"] = "password-" + id;
            values[prefix + "BaseUrl"] = "https://askue.example/api/";
            values[prefix + "TimeZoneId"] = "UTC";
            values[prefix + "DaysBack"] = "0";
            values[prefix + "PointIds"] = "101";
        }
        return values;
    }
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }
    private sealed class Handler(string expectedCredentials, double power, bool fail = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(expectedCredentials, Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
            if (fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            object data = request.RequestUri!.AbsolutePath.EndsWith("/points", StringComparison.Ordinal)
                ? new[] { new { id = 101, name = "Shared meter ID", scale = 1 } }
                : request.RequestUri.AbsolutePath.EndsWith("/profile/1", StringComparison.Ordinal)
                    ? new[] { new { step = 1800, date = At.AddHours(-11).ToUnixTimeSeconds(), value = power } } : Array.Empty<object>();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(data) });
        }
    }
}
