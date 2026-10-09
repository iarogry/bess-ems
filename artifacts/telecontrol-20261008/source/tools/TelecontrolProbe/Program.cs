using System.Text.Json;
using BatteryEms.Adapters.Telecontrol;
using BatteryEms.Application.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

// Uses the same adapter and stores as EMS. Only Telecontrol entries are imported from .env.
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var values = File.ReadLines(Path.Combine(root, ".env"))
    .Where(line => line.StartsWith("Telecontrol__", StringComparison.Ordinal))
    .Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
    .ToDictionary(pair => pair[0].Replace("__", ":", StringComparison.Ordinal), pair => (string?)pair[1], StringComparer.Ordinal);
var configuration = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(root, "config/examples/telecontrol.novobudov6.json"))
    .AddInMemoryCollection(values).Build();
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
services.AddSingleton<ISiteTelemetryStore>(_ => new InMemorySiteTelemetryStore(TimeSpan.FromMinutes(2)));
services.AddSingleton<IChpTelemetryStore, InMemoryChpTelemetryStore>();
services.AddTelecontrolTelemetry(configuration);
await using var provider = services.BuildServiceProvider();
_ = provider.GetRequiredService<IOptions<TelecontrolOptions>>().Value;
try { _ = await provider.GetRequiredService<TelecontrolClient>().ReadAsync(DateTimeOffset.UtcNow, CancellationToken.None); }
catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
{
    await Console.Error.WriteLineAsync(exception.GetType().Name + ": " + exception.Message);
    if (exception.InnerException is { } inner) { await Console.Error.WriteLineAsync(inner.GetType().Name + ": " + inner.Message); }
    Environment.ExitCode = 1;
    return;
}
var service = provider.GetServices<IHostedService>().Single();
await service.StartAsync(CancellationToken.None);
try
{
    for (var attempt = 0; attempt < 30; attempt++)
    {
        var reading = provider.GetRequiredService<IChpTelemetryStore>().GetLatest("telecontrol-5552", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        if (reading is not null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { reading.DeviceId, reading.AssetId, reading.Timestamp,
                reading.ReceivedAt, reading.SourceTimestamp, reading.PowerKw, reading.Quality,
                Parameters = reading.Parameters.Count, Messages = reading.Messages.Count,
                OperatingHours = reading.Parameters.FirstOrDefault(point => point.Name == "Betriebsstunden")?.NumericValue },
                new JsonSerializerOptions { WriteIndented = true }));
            if (reading.Parameters.Count == 0) { Environment.ExitCode = 1; }
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
    Console.Error.WriteLine("Telecontrol live read timed out.");
    Environment.ExitCode = 1;
}
finally { await service.StopAsync(CancellationToken.None); }
