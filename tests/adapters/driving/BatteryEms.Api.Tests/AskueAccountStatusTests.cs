using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Application.Site;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class AskueAccountStatusTests
{
    [Fact]
    public async Task Status_keeps_sites_separate_and_preserves_last_success_after_failure()
    {
        using var factory = new BatteryEmsApiFactory();
        var store = factory.Services.GetRequiredService<ISiteConsumptionPollStatusStore>();
        var at = DateTimeOffset.UtcNow;
        store.Started("account-a", "site-a", at);
        store.Succeeded("account-a", "site-a", at, 12);
        store.Started("account-a", "site-a", at.AddMinutes(30));
        store.Failed("account-a", "site-a", at.AddMinutes(30), "authentication_failed");
        store.Started("account-b", "site-b", at);
        store.Succeeded("account-b", "site-b", at, 20);
        using var client = factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/sites/askue/status");
        var accounts = body.GetProperty("accounts");
        Assert.Equal(2, accounts.GetArrayLength());
        Assert.Equal("site-a", accounts[0].GetProperty("site_id").GetString());
        Assert.Equal("failed", accounts[0].GetProperty("state").GetString());
        Assert.Equal(12, accounts[0].GetProperty("last_imported_readings").GetInt32());
        Assert.Equal(at, accounts[0].GetProperty("last_success_at").GetDateTimeOffset());
        Assert.Equal("ok", accounts[1].GetProperty("state").GetString());
        Assert.Equal(20, accounts[1].GetProperty("last_imported_readings").GetInt32());
        Assert.All(accounts.EnumerateArray(), item =>
        {
            Assert.False(item.TryGetProperty("username", out _));
            Assert.False(item.TryGetProperty("password", out _));
        });
    }
}
