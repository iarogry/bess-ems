using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatteryEms.Adapters.FusionSolar;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json reflection.")]
internal sealed class FusionSolarLoginRequest
{
    [JsonPropertyName("userName")]
    public required string UserName { get; init; }

    [JsonPropertyName("systemCode")]
    public required string SystemCode { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json reflection.")]
internal sealed class FusionSolarStationKpiRequest
{
    [JsonPropertyName("stationCodes")]
    public required string StationCodes { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json reflection.")]
internal sealed class FusionSolarEnvelope<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("failCode")]
    public int? FailCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json reflection.")]
internal sealed class FusionSolarRawEnvelope
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("failCode")]
    public int? FailCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json reflection.")]
internal sealed class FusionSolarStationKpi
{
    public bool IsTimestampSubstituted { get; init; }

    [JsonPropertyName("stationCode")]
    public string StationCode { get; init; } = string.Empty;

    [JsonPropertyName("collectTime")]
    public long? CollectTime { get; init; }

    [JsonPropertyName("dataItemMap")]
    public IReadOnlyDictionary<string, double?> DataItemMap { get; init; } =
        new Dictionary<string, double?>(StringComparer.Ordinal);
}

internal sealed class FusionSolarApiException : InvalidOperationException
{
    public FusionSolarApiException()
    {
    }

    public FusionSolarApiException(string message)
        : base(message)
    {
    }

    public FusionSolarApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FusionSolarApiException(string path, int? failCode, string? message, string? data)
        : base($"FusionSolar API error on '{path}': code={failCode}, message='{message}', data='{data}'.")
    {
        Path = path;
        FailCode = failCode;
        ApiMessage = message;
        ApiData = data;
    }

    public string Path { get; } = string.Empty;

    public int? FailCode { get; }

    public string? ApiMessage { get; }

    public string? ApiData { get; }
}
