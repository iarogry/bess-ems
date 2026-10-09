using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Api.Composition;
using BatteryEms.Application.Site;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class SiteCatalogTests
{
    [Fact]
    public async Task Catalog_survives_host_recreation_without_assuming_source_bindings()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Sites:Catalog:0:SiteId"] = "site-address-96",
            ["Sites:Catalog:0:Name"] = "Українська 96",
            ["Sites:Catalog:1:SiteId"] = "site-address-5",
            ["Sites:Catalog:1:Name"] = "Цурюпи 5"
        };
        for (var restart = 0; restart < 2; restart++)
        {
            using var factory = new BatteryEmsApiFactory().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));
            using var client = factory.CreateClient();
            var body = await client.GetFromJsonAsync<JsonElement>("/sites");
            Assert.Equal(2, body.GetProperty("sites").GetArrayLength());
            var registry = factory.Services.GetRequiredService<ISiteRegistry>();
            Assert.Equal("Українська 96", registry.Find("site-address-96")!.Name);
            Assert.All(registry.All(), site =>
            {
                Assert.Empty(site.AssetIds);
                Assert.Empty(site.PvSourceRefs);
                Assert.Empty(site.GridConnections);
                Assert.Empty(site.Instruments.Values);
            });
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("SITE-A")]
    public void Catalog_rejects_empty_or_duplicate_ids(string secondId)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sites:Catalog:0:SiteId"] = "site-a", ["Sites:Catalog:0:Name"] = "A",
            ["Sites:Catalog:1:SiteId"] = secondId, ["Sites:Catalog:1:Name"] = "B"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddConfiguredSiteCatalog();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISiteRegistry>());
    }
}
