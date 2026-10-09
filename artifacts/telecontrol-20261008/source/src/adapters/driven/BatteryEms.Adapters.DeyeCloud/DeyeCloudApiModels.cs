using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace BatteryEms.Adapters.DeyeCloud;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated automatically via System.Text.Json reflection")]
internal sealed class DeyeApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("msg")]
    public string Msg { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("tokenType")]
    public string? TokenType { get; init; }

    [JsonPropertyName("expiresIn")]
    public long? ExpiresIn { get; init; }

    [JsonPropertyName("generationPower")]
    public double? GenerationPower { get; init; }

    [JsonPropertyName("consumptionPower")]
    public double? ConsumptionPower { get; init; }

    [JsonPropertyName("gridPower")]
    public double? GridPower { get; init; }

    [JsonPropertyName("purchasePower")]
    public double? PurchasePower { get; init; }

    [JsonPropertyName("wirePower")]
    public double? WirePower { get; init; }

    [JsonPropertyName("chargePower")]
    public double? ChargePower { get; init; }

    [JsonPropertyName("dischargePower")]
    public double? DischargePower { get; init; }

    [JsonPropertyName("batteryPower")]
    public double? BatteryPower { get; init; }

    [JsonPropertyName("batterySOC")]
    public double? BatterySoc { get; init; }

    [JsonPropertyName("irradiateIntensity")]
    public double? IrradiateIntensity { get; init; }

    [JsonIgnore]
    public bool IsSuccess => Code == 0 || Code == 1000000;
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes")]
internal sealed class TokenRequest
{
    [JsonPropertyName("appSecret")]
    public required string AppSecret { get; init; }

    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("password")]
    public required string Password { get; init; }

    [JsonPropertyName("companyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CompanyId { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes")]
internal sealed class TokenData
{
    [JsonPropertyName("accessToken")]
    public string Token { get; init; } = string.Empty;

    [JsonPropertyName("expiresIn")]
    public long? ExpiresIn { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes")]
internal sealed class DeviceLatestRequest
{
    [JsonPropertyName("deviceList")]
    public required IReadOnlyList<string> DeviceList { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes")]
internal sealed class StationLatestRequest
{
    [JsonPropertyName("stationId")]
    public required string StationId { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes")]
internal sealed class DeviceLatestData
{
    [JsonPropertyName("deviceSn")]
    public string DeviceSn { get; init; } = string.Empty;

    [JsonPropertyName("dataList")]
    public IReadOnlyList<DataPoint> DataList { get; init; } = [];
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes")]
internal sealed class DataPoint
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = string.Empty;
}
