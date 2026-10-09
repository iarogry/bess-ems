using System.Globalization;
using System.Text.Json;
using BatteryEms.Application.Realtime;
using BatteryEms.Domain;

namespace BatteryEms.Adapters.Telecontrol;

public static class TelecontrolPayload
{
    public static ChpTelemetry Parse(JsonElement envelope, TelecontrolOptions options, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("dataPoints", out var points) || points.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Telecontrol telemetry data is absent. Envelope fields: "
                + string.Join(",", envelope.EnumerateObject().Select(property => property.Name)));
        }
        var parameters = new List<ChpParameter>();
        var powers = new List<double?>();
        foreach (var point in points.EnumerateArray())
        {
            var name = ReadText(point, "name") ?? string.Empty;
            var block = ReadText(point, "dataBlockName");
            var unit = ReadText(point, "unit") ?? string.Empty;
            var raw = point.TryGetProperty("value", out var value) && value.ValueKind != JsonValueKind.Null
                ? value.ToString() : null;
            var number = ParseNumber(raw);
            var available = raw is not null && raw != "---";
            if (unit == "°C" && (number is null || number < -273.15)) { available = false; number = null; }
            if (raw is "NaN" or "Infinity" or "-Infinity") { available = false; }
            parameters.Add(new ChpParameter(name, unit, raw, number, available));
            if (name == "Leistung" && block == "Leistung")
            {
                // Live payload values are already in kW. Never substitute a setpoint or energy.
                var scaleOk = !point.TryGetProperty("scale", out var scale) || scale.TryGetInt32(out var n) && n == 0;
                powers.Add(unit == "kW" && scaleOk && number >= 0 ? number : null);
            }
        }
        return CreateTelemetry(data, options, receivedAt, parameters, powers);
    }

    private static ChpTelemetry CreateTelemetry(JsonElement data, TelecontrolOptions options,
        DateTimeOffset receivedAt, List<ChpParameter> parameters, List<double?> powers)
    {
        var sourceTime = ReadText(data, "packageDateTime");
        var timestamp = ParseTimestamp(sourceTime, options.SourceTimeZoneId);
        var power = powers.Count == 1 ? powers[0] : null;
        var quality = timestamp is null || timestamp > receivedAt.AddSeconds(30)
            ? DataQuality.ProtocolError("telecontrol-measurement-time-missing-invalid-or-future")
            : power is null ? DataQuality.ProtocolError("telecontrol-power-missing-invalid-or-duplicate")
            : receivedAt - timestamp > TimeSpan.FromSeconds(options.MaxMeasurementAgeSeconds)
                ? DataQuality.Stale("telecontrol-measurement-aged")
            : options.ClockConfirmed ? DataQuality.Valid
                : DataQuality.Substituted("telecontrol-source-timezone-provisional");
        return new ChpTelemetry(options.AssetId, options.DeviceId, timestamp ?? receivedAt, receivedAt,
            sourceTime, quality.Flag == DataQualityState.ProtocolError ? null : power, quality, parameters, ReadMessages(data));
    }

    private static List<ChpMessage> ReadMessages(JsonElement data)
    {
        var messages = new List<ChpMessage>();
        if (data.TryGetProperty("messages", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in items.EnumerateArray())
            {
                if (message.TryGetProperty("code", out var code) && code.TryGetInt32(out var codeValue)
                    && message.TryGetProperty("messageType", out var type) && type.TryGetInt32(out var typeValue))
                {
                    messages.Add(new ChpMessage(ReadText(message, "name") ?? string.Empty, codeValue, typeValue));
                }
            }
        }
        return messages;
    }

    private static string? ReadText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static double? ParseNumber(string? text) =>
        double.TryParse(text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) ? value : null;

    private static DateTimeOffset? ParseTimestamp(string? text, string zoneId)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        // Explicit offsets take precedence over any configured wall-clock timezone.
        if ((text.EndsWith('Z') || text.Length > 19 && (text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10))
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)) { return offset; }
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { return null; }
        var wall = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        if (zone.IsInvalidTime(wall) || zone.IsAmbiguousTime(wall)) { return null; }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone), TimeSpan.Zero);
    }
}
