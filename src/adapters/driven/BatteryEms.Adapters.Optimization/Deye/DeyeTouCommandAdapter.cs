using System.Net.Http.Json;
using System.Text.Json;

namespace BatteryEms.Adapters.Optimization.Deye;

public sealed record DeyeTouGuardState(
    bool MasterOnline,
    bool SlaveLinkHealthy,
    bool BmsFresh,
    double BmsSocPercent,
    double UpsReserveSocPercent,
    double RequestedPowerKw);

public static class DeyeWriteOptions
{
    /// <summary>Reads the explicit opt-in flag; any value other than TRUE keeps writes disabled.</summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var value = environment is null
            ? Environment.GetEnvironmentVariable("DEYE_WRITE_ENABLED")
            : environment.GetValueOrDefault("DEYE_WRITE_ENABLED");
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record DeyeTouInterval(string Time, bool EnableGeneration, bool EnableGridCharge, bool EnableSell, int Power, int Soc, int Voltage);
public sealed record DeyeOrderResult(long OrderId, int Status, bool Success, string? Message, string? Error, string RawJson);

public interface IDeyeTouCommandAdapter
{
    Task<string> UpdateAsync(string deviceSn, IReadOnlyList<DeyeTouInterval> intervals, DeyeTouGuardState guards, CancellationToken cancellationToken);
    Task<string> SwitchAsync(string deviceSn, bool enabled, DeyeTouGuardState guards, CancellationToken cancellationToken);
    Task<DeyeOrderResult> WaitForResultAsync(string orderId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Fail-closed Deye TOU writer. Intended for the master inverter only.</summary>
public sealed class DeyeTouCommandAdapter : IDeyeTouCommandAdapter
{
    private readonly HttpClient _http;
    private readonly Uri _updateEndpoint;
    private readonly Uri _switchEndpoint;
    private readonly Uri _resultEndpoint;
    private readonly string _masterSerial;
    private readonly bool _writeEnabled;

    public DeyeTouCommandAdapter(HttpClient http, Uri baseUri, string masterSerial, bool writeEnabled = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _updateEndpoint = new Uri(baseUri, "order/sys/tou/update");
        _switchEndpoint = new Uri(baseUri, "order/sys/tou/switch");
        _resultEndpoint = new Uri(baseUri, "order/");
        _masterSerial = string.IsNullOrWhiteSpace(masterSerial) ? throw new ArgumentException("Master serial is required.", nameof(masterSerial)) : masterSerial;
        _writeEnabled = writeEnabled;
    }

    public async Task<string> UpdateAsync(string deviceSn, IReadOnlyList<DeyeTouInterval> intervals, DeyeTouGuardState guards, CancellationToken cancellationToken)
    {
        EnsureWritable(deviceSn, guards);
        ValidateIntervals(intervals);
        using var response = await _http.PostAsJsonAsync(_updateEndpoint, new { deviceSn, timeUseSettingItems = intervals }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SwitchAsync(string deviceSn, bool enabled, DeyeTouGuardState guards, CancellationToken cancellationToken)
    {
        EnsureWritable(deviceSn, guards);
        using var response = await _http.PostAsJsonAsync(_switchEndpoint, new { deviceSn, action = enabled ? "on" : "off" }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeyeOrderResult> WaitForResultAsync(string orderId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orderId)) throw new ArgumentException("orderId is required.", nameof(orderId));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var delay = TimeSpan.FromSeconds(1);
        try
        {
            while (true)
            {
                using var response = await _http.GetAsync(new Uri(_resultEndpoint, Uri.EscapeDataString(orderId)), timeoutCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                var result = ParseOrderResult(orderId, raw);
                if (result.Status is 666 or 400 or 500 || !result.Success)
                {
                    if (result.Status != 666) throw new InvalidOperationException($"Deye order {orderId} failed: {result.Error ?? result.Message ?? "unknown error"}.");
                    return result;
                }
                await Task.Delay(delay, timeoutCts.Token).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 1.5));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"Deye order {orderId} did not complete within {timeout}."); }
    }

    public static string ExtractOrderId(string commandResponse)
    {
        using var doc = JsonDocument.Parse(commandResponse);
        var root = doc.RootElement;
        if (root.TryGetProperty("orderId", out var direct) && direct.ValueKind != JsonValueKind.Null) return direct.ToString();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("orderId", out var nested)) return nested.ToString();
        throw new InvalidOperationException("Deye command response did not contain orderId.");
    }

    private static DeyeOrderResult ParseOrderResult(string orderId, string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var id = data.TryGetProperty("orderId", out var idValue) && idValue.TryGetInt64(out var parsedId)
            ? parsedId : long.TryParse(orderId, out var fallback) ? fallback : 0;
        var status = data.TryGetProperty("status", out var statusValue) && statusValue.TryGetInt32(out var parsedStatus) ? parsedStatus : 0;
        var success = !data.TryGetProperty("success", out var successValue) || successValue.ValueKind != JsonValueKind.False;
        var message = data.TryGetProperty("msg", out var msg) ? msg.ToString() : null;
        var error = data.TryGetProperty("error", out var err) ? err.ToString() : null;
        return new DeyeOrderResult(id, status, success, message, error, raw);
    }

    private void EnsureWritable(string deviceSn, DeyeTouGuardState g)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (!_writeEnabled) throw new InvalidOperationException("DEYE_WRITE_ENABLED is false.");
        if (!string.Equals(deviceSn, _masterSerial, StringComparison.Ordinal)) throw new InvalidOperationException("Only the configured master inverter may be written.");
        if (!g.MasterOnline || !g.SlaveLinkHealthy || !g.BmsFresh) throw new InvalidOperationException("Deye/BMS network guard failed.");
        if (g.BmsSocPercent < g.UpsReserveSocPercent) throw new InvalidOperationException("BMS SOC is below UPS reserve.");
        if (Math.Abs(g.RequestedPowerKw) > 160) throw new InvalidOperationException("Requested power exceeds aggregate 160 kW limit.");
    }

    private static void ValidateIntervals(IReadOnlyList<DeyeTouInterval>? intervals)
    {
        if (intervals is null || intervals.Count != 6)
            throw new ArgumentException("Deye TOU requires exactly 6 intervals covering the full day.", nameof(intervals));

        // Deye interprets each item as the start of a continuous period.  A
        // missing/duplicate start silently creates an unintended gap, so fail
        // closed before any network call.
        if (!TimeSpan.TryParseExact(intervals[0].Time, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out var first) || first != TimeSpan.Zero)
            throw new ArgumentException("The first TOU interval must start at 00:00 (HH:mm).", nameof(intervals));

        var previous = first;
        for (var i = 1; i < intervals.Count; i++)
        {
            if (!TimeSpan.TryParseExact(intervals[i].Time, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out var current) || current <= previous)
                throw new ArgumentException("TOU interval starts must be strictly increasing HH:mm values.", nameof(intervals));
            previous = current;
        }
    }
}
