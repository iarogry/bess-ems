using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace BatteryEms.Adapters.Askue;

internal sealed record AskuePoint(string Id, string Name, double Scale = 1.0);

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json reflection.")]
internal sealed class AskueProfilePoint
{
    [JsonPropertyName("date")]
    public long Date { get; init; }

    [JsonPropertyName("step")]
    public int Step { get; init; }

    [JsonPropertyName("value")]
    public double Value { get; init; }
}
