using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;

namespace BatteryEms.Adapters.DeyeCloud;

public sealed record DeyeBrokerDeviceOptions(string SiteId, string StationId,
    string MasterSerial, string SlaveSerial, bool WriteEnabled = false, int MaximumReadOrders = 9)
{
    public DeyeBrokerDeviceOptions EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(StationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(MasterSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(SlaveSerial);
        if (MasterSerial == SlaveSerial) { throw new ArgumentException("Distinct master and slave identities are required."); }
        if (MaximumReadOrders is < 1 or > 9) { throw new ArgumentOutOfRangeException(nameof(MaximumReadOrders)); }
        return this;
    }
}

// Only the application-server broker may own this driver and its authenticated
// HttpClient. The separate broker creates one driver/client per attempt. No mutation retry handler
// may be attached to the injected client; vendor token refresh belongs before
// this boundary, never in a replay of a mutation request.
public sealed class DeyeCloudDeviceWriteBrokerDriver : IDeviceWriteBrokerDriver
{
    private static readonly string[] AlarmKeys = ["alarm", "fault", "error", "warning", "outage"];
    private readonly HttpClient _http;
    private readonly DeyeBrokerDeviceOptions _options;
    private readonly IClock _clock;
    private readonly IDeviceWriteBrokerMutationGate _gate;

    public DeyeCloudDeviceWriteBrokerDriver(HttpClient http, DeyeBrokerDeviceOptions options, IClock clock,
        IDeviceWriteBrokerMutationGate gate)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).EnsureValid();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        ValidateBaseAddress(_http.BaseAddress);
    }

    // Production composition must use this dedicated client, with a token
    // acquired before admission. No auth-refresh replay, redirects or generic
    // resilience handler may repeat an already sent mutation.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "The returned HttpClient owns the SocketsHttpHandler through disposeHandler:true; its caller owns and disposes the client.")]
    public static HttpClient CreateAuthenticatedClient(Uri baseAddress, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ValidateBaseAddress(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: true)
        {
            BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        client.DefaultRequestHeaders.Authorization = authorization;
        return client;
    }

    private static void ValidateBaseAddress(Uri? address)
    {
        if (address is not { IsAbsoluteUri: true, Scheme: "https" }
            || !address.AbsolutePath.EndsWith('/')
            || !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query)
            || !string.IsNullOrEmpty(address.Fragment))
        { throw new ArgumentException("A server-configured HTTPS vendor base ending in '/' is required.", nameof(address)); }
    }

    public async Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!_options.WriteEnabled || envelope.Attempt.SiteId != _options.SiteId) { return false; }
        envelope.EnsureValid(_clock.UtcNow);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        return (await ReadPreflightAsync(bounded.Token).ConfigureAwait(false)).Ready;
    }

    public async Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(
        DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!_options.WriteEnabled || envelope.Attempt.SiteId != _options.SiteId)
        { return DeviceWriteBrokerReadback.Unknown; }
        envelope.EnsureValid(_clock.UtcNow);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(120));
        // Fresh topology/telemetry/capability is read again, not reused from the
        // earlier admission preflight or another attempt.
        var preflight = await ReadPreflightAsync(bounded.Token).ConfigureAwait(false);
        if (!preflight.Ready) { return DeviceWriteBrokerReadback.Unknown; }
        if (!await _gate.CanSendAsync(envelope.Attempt with { NowUtc = _clock.UtcNow }, bounded.Token).ConfigureAwait(false))
        { return DeviceWriteBrokerReadback.Unknown; }
        envelope.EnsureValid(_clock.UtcNow);
        if (_clock.UtcNow.ToUnixTimeSeconds() - preflight.Oldest is < 0 or > 600)
        { return DeviceWriteBrokerReadback.Unknown; }
        var expected = envelope.Window.Intervals.Skip(1).Append(envelope.Window.Intervals[0]).ToArray();
        var items = expected.Select(interval => ToWire(interval, preflight.EnableSell)).ToArray();
        var update = await PostAsync("order/sys/tou/update",
            new { deviceSn = _options.MasterSerial, timeUseSettingItems = items }, bounded.Token).ConfigureAwait(false);
        var updateId = OrderId(update);
        // Exactly one update call above. Every subsequent call is read-only.
        try
        {
            // Order status is diagnostic. The independently read device state
            // below is the only proof that the requested settings are active.
            await WaitForUpdateAsync(updateId, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The 45-second order poll timed out; fresh readback can still prove
            // the desired state without ever repeating the update.
        }
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(bounded.Token);
        readDeadline.CancelAfter(TimeSpan.FromSeconds(45));
        for (var attempt = 0; attempt < _options.MaximumReadOrders; attempt++)
        {
            var read = await PostAsync("strategy/dynamicControl/read",
                new { deviceSn = _options.MasterSerial }, readDeadline.Token).ConfigureAwait(false);
            RequireSuccess(read, allowReadAccepted: true);
            var readId = OrderId(read);
            using var pendingDeadline = CancellationTokenSource.CreateLinkedTokenSource(readDeadline.Token);
            pendingDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                while (true)
                {
                    var snapshot = await PostAsync("strategy/dynamicControl/readResult",
                        new { orderId = WireOrderId(readId) }, pendingDeadline.Token).ConfigureAwait(false);
                    if (snapshot["success"]?.ToString() == "false") { break; }
                    RequireSuccess(snapshot, allowReadAccepted: true);
                    if (snapshot["orderId"] is not null && snapshot["orderId"]!.ToString() != readId)
                    { throw new InvalidOperationException("deye-read-order-mismatch"); }
                    if (snapshot["timeUseSettingItems"] is JsonArray actual)
                    {
                        if (snapshot["success"]?.ToString() == "true" && snapshot["touAction"]?.ToString() == "on"
                            && Matches(actual, expected, preflight.EnableSell))
                        { return DeviceWriteBrokerReadback.Matched; }
                        break; // Completed snapshots never change; next attempt creates a new read order.
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), pendingDeadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (pendingDeadline.IsCancellationRequested && !readDeadline.IsCancellationRequested)
            {
                // A pending read may expire independently; request a new read
                // within the same overall verification deadline.
            }
            if (attempt + 1 < _options.MaximumReadOrders)
            { await Task.Delay(TimeSpan.FromSeconds(5), readDeadline.Token).ConfigureAwait(false); }
        }
        return DeviceWriteBrokerReadback.Unknown;
    }

    private async Task<(bool Ready, bool EnableSell, long Oldest)> ReadPreflightAsync(CancellationToken token)
    {
        var devices = await PostAsync("station/device", new
        {
            stationIds = new[] { WireOrderId(_options.StationId) }, page = 1, size = 20,
        }, token).ConfigureAwait(false);
        RequireSuccess(devices);
        var rows = (devices["deviceListItems"] ?? devices["data"]?["deviceListItems"]) as JsonArray;
        if (rows is null) { return (false, false, 0); }
        var inverters = rows.OfType<JsonObject>().Where(row =>
            string.Equals(row["deviceType"]?.ToString(), "INVERTER", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (inverters.Length != 2 || inverters.Count(row => row["deviceSn"]?.ToString() == _options.MasterSerial) != 1
            || inverters.Count(row => row["deviceSn"]?.ToString() == _options.SlaveSerial) != 1
            || inverters.Any(row => (row["connectStatus"] ?? row["deviceState"])?.ToString() is not ("1" or "true" or "True")))
        { return (false, false, 0); }
        var latest = await PostAsync("device/latest",
            new { deviceList = new[] { _options.MasterSerial, _options.SlaveSerial } }, token).ConfigureAwait(false);
        RequireSuccess(latest);
        var telemetry = (latest["deviceDataList"] ?? latest["data"]) as JsonArray;
        if (telemetry is null || !TelemetryReady(telemetry, out var oldest)) { return (false, false, 0); }
        var tou = await PostAsync("config/tou", new { deviceSn = _options.MasterSerial }, token).ConfigureAwait(false);
        RequireSuccess(tou);
        var system = await PostAsync("config/system", new { deviceSn = _options.MasterSerial }, token).ConfigureAwait(false);
        RequireSuccess(system);
        var settings = tou["timeUseSettingItems"] as JsonArray;
        if (settings is null || settings.Count != 6 || settings.Any(item => item is not JsonObject)) { return (false, false, 0); }
        var supported = settings.OfType<JsonObject>().Count(item => item.ContainsKey("enableSell"));
        if (supported is not (0 or 6)) { return (false, false, 0); }
        if (_clock.UtcNow.ToUnixTimeSeconds() - oldest is < 0 or > 600) { return (false, false, 0); }
        return (true, supported == 6, oldest);
    }

    private bool TelemetryReady(JsonArray telemetry, out long oldest)
    {
        oldest = long.MaxValue;
        var aggregatePower = 0d;
        foreach (var serial in new[] { _options.MasterSerial, _options.SlaveSerial })
        {
            var matches = telemetry.OfType<JsonObject>().Where(row => row["deviceSn"]?.ToString() == serial).ToArray();
            if (matches.Length != 1 || !TelemetryRowReady(matches[0], serial == _options.MasterSerial,
                    out var collected, out var power)) { return false; }
            oldest = Math.Min(oldest, collected);
            aggregatePower += Math.Abs(power);
        }
        return aggregatePower <= 160000 && _clock.UtcNow.ToUnixTimeSeconds() - oldest is >= 0 and <= 600;
    }

    private static bool TelemetryRowReady(JsonObject row, bool master, out long collected, out double power)
    {
        collected = 0;
        power = 0;
        if (!long.TryParse(row["collectionTime"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out collected) || row["dataList"] is not JsonArray points) { return false; }
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in points)
        {
            if (node is not JsonObject point) { return false; }
            var key = point["key"]?.ToString();
            if (string.IsNullOrWhiteSpace(key) || !map.TryAdd(key, point["value"]?.ToString() ?? string.Empty)) { return false; }
        }
        var alarms = map.Where(point => IsAlarmKey(point.Key)).ToArray();
        if (alarms.Length == 0 || alarms.Any(point => !IsClear(point.Value))
            || !Number(map, "batteryPower", out power) || Math.Abs(power) > 80000) { return false; }
        return !master || (Number(map, "BMSSOC", out var soc) && soc is >= 30 and <= 100
            && Number(map, "BMSVoltage", out var voltage) && voltage > 0);
    }

    private async Task<bool> WaitForUpdateAsync(string id, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            using var response = await _http.GetAsync(new Uri("order/" + Uri.EscapeDataString(id), UriKind.Relative), deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false))?.AsObject()
                ?? throw new InvalidOperationException("deye-order-response-invalid");
            var data = root["data"] as JsonObject ?? root;
            if (data["orderId"] is not null && data["orderId"]!.ToString() != id) { return false; }
            if (data["success"]?.ToString() == "false") { return false; }
            RequireSuccess(root);
            var status = data["status"]?.ToString();
            if (status == "666") { return true; }
            if (status is "400" or "500") { return false; }
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject> PostAsync(string path, object body, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidOperationException("deye-response-invalid");
    }

    private static void RequireSuccess(JsonObject root, bool allowReadAccepted = false)
    {
        var code = root["code"]?.ToString();
        if (root["success"]?.ToString() == "false" || (code is not (null or "0" or "1000000")
            && !(allowReadAccepted && code == "1106000")))
        { throw new InvalidOperationException("deye-request-rejected"); }
    }

    private static string OrderId(JsonObject root)
    {
        RequireSuccess(root, allowReadAccepted: true);
        var id = (root["orderId"] ?? root["data"]?["orderId"])?.ToString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) { throw new InvalidOperationException("deye-order-id-invalid"); }
        return id;
    }

    private static object WireOrderId(string value) => long.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var number) ? number : value;

    private static JsonObject ToWire(ShadowTouInterval row, bool sell)
    {
        var item = new JsonObject
        {
            ["time"] = row.StartTime, ["enableGeneration"] = row.EnableGeneration,
            ["enableGridCharge"] = row.EnableGridCharge, ["power"] = row.PowerWatts,
            ["soc"] = row.SocPercent, ["voltage"] = row.Voltage,
        };
        if (sell) { item["enableSell"] = row.EnableSell; }
        return item;
    }

    private static bool Matches(JsonArray actual, ShadowTouInterval[] expected, bool sell)
    {
        if (actual.Count != expected.Length) { return false; }
        for (var index = 0; index < expected.Length; index++)
        {
            if (actual[index] is not JsonObject row) { return false; }
            var wanted = ToWire(expected[index], sell);
            if (!int.TryParse(row["power"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var power)
                || (power != expected[index].PowerWatts && power != expected[index].PowerWatts / 10 * 10)) { return false; }
            foreach (var key in wanted.Select(pair => pair.Key).Where(key => key != "power"))
            {
                if (key == "time")
                {
                    if (NormalizeTime(row[key]?.ToString()) != expected[index].StartTime) { return false; }
                    continue;
                }
                if (row[key]?.ToString() != wanted[key]?.ToString()) { return false; }
            }
        }
        return true;
    }

    private static string? NormalizeTime(string? value)
    {
        if (value is null) { return null; }
        if (TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var time)
            || (TimeSpan.TryParseExact(value, "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out time) && time.Seconds == 0))
        { return time.ToString("hh\\:mm", CultureInfo.InvariantCulture); }
        return null;
    }

    private static bool Number(Dictionary<string, string> points, string key, out double number)
    {
        number = double.NaN;
        return points.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }

    private static bool IsAlarmKey(string key) => AlarmKeys
        .Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static bool IsClear(string value) => value.Trim().ToUpperInvariant() is "0" or "0.0" or "FALSE" or "NONE" or "NORMAL" or "OK" or "NO";
}
