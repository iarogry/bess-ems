using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using BatteryEms.Adapters.Askue;
using BatteryEms.Application.Site;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace BatteryEms.Adapters.Askue.Tests;

public static class AskueLivePoll
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task RunAsync()
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddAskueSiteConsumption(config, "live-test-site");
        services.AddSingleton<ISiteConsumptionStore, ConsoleSiteConsumptionStore>();
        
        var sp = services.BuildServiceProvider();
        var collector = sp.GetRequiredService<AskueSiteConsumptionCollector>();
        
        LogToConsole("Starting real ASKUE poll...");
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        var count = await collector.CollectAsync(date, CancellationToken.None);
        LogToConsole($"Poll finished. Imported {count} readings.");
    }

    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters")]
    private static void LogToConsole(string message) => Console.WriteLine(message);

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes")]
    private sealed class ConsoleSiteConsumptionStore : ISiteConsumptionStore
    {
        public Task AppendAsync(IReadOnlyList<SiteConsumptionReading> readings, CancellationToken cancellationToken)
        {
            // Беремо лише перші 2 записи для кожної точки, щоб не захаращувати вивід
            foreach (var r in readings.Take(2))
            {
                Console.WriteLine(JsonSerializer.Serialize(r, JsonOptions));
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SiteConsumptionReading>> QueryAsync(SiteConsumptionQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SiteConsumptionReading>>(Array.Empty<SiteConsumptionReading>());
        }
    }
}
