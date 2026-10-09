using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatteryEms.Adapters.Optimization.Deye;

var arguments = ParseArgs(args);
var runId = Guid.NewGuid().ToString("N");
var windowId = arguments.GetValueOrDefault("window") ?? "unknown";
var scenarioDate = System.Text.RegularExpressions.Regex.Match(
    Path.GetFileName(arguments.GetValueOrDefault("scenario") ?? string.Empty),
    @"\d{4}-\d{2}-\d{2}").Value;
var auditPath = arguments.GetValueOrDefault("audit-log") ?? Path.Combine("logs", "deye-tou-agent-audit.jsonl");
var audit = new SanitizedAuditTrail(auditPath, runId, scenarioDate.Length == 10 ? scenarioDate : "unknown", windowId, "unavailable");
var stage = "runner_started";

try
{
    await audit.AppendAsync("runner", "completed", "started", details: new
    {
        mode = arguments.GetValueOrDefault("dry-run") == "true" ? "dry_run" :
            arguments.GetValueOrDefault("verify-live-only") == "true" ? "verify_live_only" : "apply"
    });
    var env = ReadEnv(arguments["env"]);
    var scenarioPath = arguments["scenario"];
    var masterSerial = Required(env, "DeyeCloud__MasterSerial");
    var baseUrl = (env.GetValueOrDefault("DeyeCloud__BaseUrl") ?? "https://eu1-developer.deyecloud.com/v1.0").TrimEnd('/') + "/";
    var writeEnabled = DeyeWriteOptions.IsEnabled(env.ToDictionary(pair => pair.Key, pair => (string?)pair.Value));

    stage = "scenario_validation";
    var scenario = JsonNode.Parse(await File.ReadAllTextAsync(scenarioPath))?.AsObject()
        ?? throw new InvalidOperationException("Scenario JSON is invalid.");
    scenarioDate = scenario["target_date"]?.ToString() ?? scenario["deliveryDate"]?.ToString() ?? scenarioDate;
    var windows = scenario["touWindows"]?.AsArray() ?? throw new InvalidOperationException("Scenario has no touWindows.");
    if (windows.Count != 4) throw new InvalidOperationException("Exactly four saved TOU windows are required.");
    var selected = windows.FirstOrDefault(x => string.Equals(x?["id"]?.GetValue<string>(), windowId, StringComparison.Ordinal))?.AsObject()
        ?? throw new InvalidOperationException("Requested saved TOU window was not found.");
    var intervalNodes = selected["intervals"]?.AsArray() ?? throw new InvalidOperationException("Selected window has no intervals.");
    if (intervalNodes.Count != 6) throw new InvalidOperationException("Selected TOU window must contain exactly six intervals.");
    var scheduleIntervals = intervalNodes.Select(ToInterval).ToArray();
    var devicePayload = DeyeTouCommandAdapter.ToDevicePayload(scheduleIntervals);
    if (scheduleIntervals.Max(x => x.Power) > 80000) throw new InvalidOperationException("Master power exceeds 80 kW.");
    var payloadHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(devicePayload)));
    audit.SetPayloadHash(payloadHash);
    var adapterVersion = typeof(DeyeTouCommandAdapter).Assembly.ManifestModule.ModuleVersionId;
    await audit.AppendAsync("scenario_validation", "completed", "passed", details: new
    {
        interval_count = scheduleIntervals.Length,
        window_count = windows.Count,
        power_limit_w = 80000,
        device_times = devicePayload.Select(x => x.Time).ToArray(),
        expected_payload = devicePayload,
        adapter_version = adapterVersion
    });

    if (arguments.GetValueOrDefault("dry-run") == "true")
    {
        await audit.AppendAsync("dry_run", "completed", "success");
        Console.WriteLine(JsonSerializer.Serialize(new { dry_run = true, window = windowId, payload_hash = payloadHash, adapter_version = adapterVersion, device_payload = devicePayload }));
        return;
    }
    // Emit before network access so an interrupted run still identifies its input.
    Console.WriteLine(JsonSerializer.Serialize(new { stage = "selected", window = windowId, payload_hash = payloadHash, adapter_version = adapterVersion, device_payload = devicePayload }));

    using var http = new HttpClient(new AuditedHttpHandler(audit))
    {
        BaseAddress = new Uri(baseUrl),
        Timeout = TimeSpan.FromSeconds(30)
    };
    stage = "token_acquisition";
    var token = await GetTokenAsync(http, env, audit);
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    if (arguments.GetValueOrDefault("verify-live-only") == "true")
    {
        stage = "live_readback";
        var liveOnly = await WaitForLiveReadBackAsync(http, masterSerial, devicePayload, compareEnableSell: false, TimeSpan.FromMinutes(2), CancellationToken.None, audit);
        await audit.AppendAsync("live_readback", "completed", liveOnly.Matches ? "matched" : "mismatch", details: new
        {
            attempts = liveOnly.Attempts,
            observed_times = liveOnly.Items.Select(x => NormalizeTime(x?["time"]?.ToString() ?? string.Empty)).ToArray()
        });
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            live_read_only = true,
            read_back_match = liveOnly.Matches,
            read_back_attempts = liveOnly.Attempts,
            read_order_id = liveOnly.ReadOrderId,
            observed_times = liveOnly.Items.Select(x => NormalizeTime(x?["time"]?.ToString() ?? string.Empty)).ToArray()
        }));
        if (!liveOnly.Matches) Environment.ExitCode = 6;
        return;
    }
    stage = "live_preflight";
    var preflight = await ReadPreflightAsync(http, env, masterSerial, 600, CancellationToken.None, audit);
    await audit.AppendAsync("live_preflight", "completed", preflight.Pass ? "passed" : "blocked", details: new
    {
        both_online = preflight.BothOnline,
        topology_healthy = preflight.TopologyHealthy,
        telemetry_fresh = preflight.AllTelemetryFresh,
        telemetry_age_seconds = preflight.TelemetryAges,
        master_soc_percent = preflight.MasterBmsSoc,
        enable_sell_supported = preflight.SupportsEnableSell
    });
    if (!preflight.Pass) throw new InvalidOperationException("Live Deye/BMS preflight failed.");
    var adapter = new DeyeTouCommandAdapter(http, new Uri(baseUrl), masterSerial, writeEnabled, preflight.SupportsEnableSell);
    var requestedPowerKw = scheduleIntervals.Max(x => Math.Abs(x.Power)) * 2d / 1000d;
    var guards = new DeyeTouGuardState(preflight.BothOnline, preflight.TopologyHealthy, preflight.AllTelemetryFresh, preflight.MasterBmsSoc, 30, requestedPowerKw);
    stage = "tou_update";
    var rawCommand = await adapter.UpdateAsync(masterSerial, scheduleIntervals, guards, CancellationToken.None);
    await audit.AppendAsync("tou_update", "completed", "request_accepted", details: new
    {
        interval_count = scheduleIntervals.Length,
        device_times = devicePayload.Select(x => x.Time).ToArray()
    });
    var orderId = DeyeTouCommandAdapter.ExtractOrderId(rawCommand);
    stage = "order_verification";
    var orderVerification = await TryWaitForOrderAsync(adapter, orderId, TimeSpan.FromSeconds(45), CancellationToken.None, audit);
    stage = "live_readback";
    var verification = await WaitForLiveReadBackAsync(
        http,
        masterSerial,
        devicePayload,
        preflight.SupportsEnableSell,
        timeout: TimeSpan.FromMinutes(2),
        CancellationToken.None,
        audit);
    await audit.AppendAsync("live_readback", "completed", verification.Matches ? "matched" : "mismatch", details: new
    {
        attempts = verification.Attempts,
        observed_times = verification.Items.Select(x => NormalizeTime(x?["time"]?.ToString() ?? string.Empty)).ToArray()
    });
    var output = new
    {
        applied = verification.Matches && orderVerification.State == "success",
        payload_hash = payloadHash,
        adapter_version = adapterVersion,
        terminal_success = orderVerification.State == "success",
        order_state = orderVerification.State,
        read_back_match = verification.Matches,
        read_back_attempts = verification.Attempts,
        read_order_id = verification.ReadOrderId,
        window = windowId,
        interval_count = scheduleIntervals.Length,
        device_times = devicePayload.Select(x => x.Time).ToArray(),
        observed_times = verification.Items.Select(x => NormalizeTime(x?["time"]?.ToString() ?? string.Empty)).ToArray(),
        enable_sell_supported = preflight.SupportsEnableSell,
        preflight_telemetry_age_seconds = preflight.TelemetryAges
    };
    await audit.AppendAsync("runner", "completed", output.applied ? "applied_and_verified" : "verification_failed", details: new
    {
        terminal_success = orderVerification.State == "success",
        read_back_match = verification.Matches,
        read_back_attempts = verification.Attempts
    });
    Console.WriteLine(JsonSerializer.Serialize(output));
    if (!output.applied) Environment.ExitCode = 6;
}
catch (Exception exception)
{
    try
    {
        await audit.AppendAsync("runner", "failed", "error", details: new
        {
            stage,
            exception_type = exception.GetType().Name
        });
    }
    catch
    {
        // Keep the original failure. A missing audit entry is detectable because
        // the preceding operation has a durable "started" event without completion.
    }
    throw;
}

static Dictionary<string, string> ReadEnv(string path)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadLines(path))
    {
        if (!line.Contains('=') || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
        var parts = line.Split('=', 2);
        result[parts[0].Trim()] = parts[1].Trim().Trim('"').Trim('\'');
    }
    return result;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Arguments must be --name value pairs.");
        result[args[i][2..]] = args[i + 1];
    }
    return result;
}

static string Required(IReadOnlyDictionary<string, string> values, string key) =>
    values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"Missing required value: {key}");

static DeyeTouInterval ToInterval(JsonNode? node)
{
    var obj = node?.AsObject() ?? throw new InvalidOperationException("Invalid TOU interval.");
    return new DeyeTouInterval(
        NormalizeTime(obj["time"]?.GetValue<string>() ?? throw new InvalidOperationException("TOU time is missing.")),
        obj["enableGeneration"]?.GetValue<bool>() ?? throw new InvalidOperationException("enableGeneration is missing."),
        obj["enableGridCharge"]?.GetValue<bool>() ?? throw new InvalidOperationException("enableGridCharge is missing."),
        obj["enableSell"]?.GetValue<bool>() ?? throw new InvalidOperationException("enableSell is missing."),
        GetInt(obj, "power"), GetInt(obj, "soc"), GetInt(obj, "voltage"));
}

static int GetInt(JsonObject obj, string key)
{
    var text = obj[key]?.ToString() ?? throw new InvalidOperationException($"{key} is missing.");
    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        return integer;
    if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric) &&
        numeric == decimal.Truncate(numeric) &&
        numeric is >= int.MinValue and <= int.MaxValue)
        return decimal.ToInt32(numeric);
    throw new InvalidOperationException($"{key} must be an integer-valued number.");
}

static string NormalizeTime(string time) =>
    time.Length == 4 && !time.Contains(':') ? $"{time[..2]}:{time[2..]}" : time;

static async Task<string> GetTokenAsync(HttpClient http, IReadOnlyDictionary<string, string> env, SanitizedAuditTrail audit)
{
    var password = Required(env, "DeyeCloud__Password");
    if (!System.Text.RegularExpressions.Regex.IsMatch(password, "^[a-fA-F0-9]{64}$"))
        password = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
    var body = new Dictionary<string, object?>
    {
        ["appSecret"] = Required(env, "DeyeCloud__AppSecret"),
        ["email"] = Required(env, "DeyeCloud__Email"),
        ["password"] = password
    };
    if (env.TryGetValue("DeyeCloud__CompanyId", out var company) && long.TryParse(company, out var companyId))
        body["companyId"] = companyId;
    using var response = await http.PostAsJsonAsync(
        $"account/token?appId={Uri.EscapeDataString(Required(env, "DeyeCloud__AppId"))}", body);
    response.EnsureSuccessStatusCode();
    var root = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject()
        ?? throw new InvalidOperationException("Token response is invalid.");
    var token = root["data"]?["accessToken"]?.GetValue<string>()
        ?? root["accessToken"]?.GetValue<string>()
        ?? throw new InvalidOperationException("Token response is empty.");
    await audit.AppendAsync("token_acquisition", "completed", "success");
    return token;
}

static async Task<ReadBackVerification> WaitForLiveReadBackAsync(
    HttpClient http,
    string masterSerial,
    IReadOnlyList<DeyeTouInterval> expected,
    bool compareEnableSell,
    TimeSpan timeout,
    CancellationToken cancellationToken,
    SanitizedAuditTrail audit)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    var attempts = 0;
    JsonArray latest = [];
    var latestReadOrderId = string.Empty;

    do
    {
        // A readResult is an immutable snapshot for one read order. Re-polling an
        // already completed order can only return the same stale TOU values, so
        // every verification attempt must request a fresh device read.
        attempts++;
        var readCommand = await PostRawObjectAsync(
            http,
            "strategy/dynamicControl/read",
            new { deviceSn = masterSerial },
            cancellationToken);
        var readCommandCode = readCommand["code"]?.ToString();
        if (readCommandCode is not ("1000000" or "1106000") || readCommand["orderId"] is null)
            throw new InvalidOperationException($"Deye live-read command was rejected with code {readCommandCode ?? "unknown"}.");
        latestReadOrderId = readCommand["orderId"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(latestReadOrderId))
            throw new InvalidOperationException("Deye live-read command returned no orderId.");
        object orderIdValue = long.TryParse(latestReadOrderId, out var numericOrderId) ? numericOrderId : latestReadOrderId;

        // A newly created read order can briefly be pending. Poll only until that
        // particular order yields its first snapshot, then create a new read order
        // if the snapshot does not yet match the requested TOU payload.
        var readOrderDeadline = Min(deadline, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15));
        do
        {
            var readBack = await PostRawObjectAsync(
                http,
                "strategy/dynamicControl/readResult",
                new { orderId = orderIdValue },
                cancellationToken);
            if (readBack["timeUseSettingItems"] is JsonArray items)
            {
                latest = items;
                if (readBack["success"]?.GetValue<bool>() != false &&
                    readBack["touAction"]?.ToString() == "on" &&
                    Compare(latest, expected, compareEnableSell))
                {
                    await audit.AppendAsync("live_readback_attempt", "completed", "matched", details: new
                    {
                        attempt = attempts,
                        observed_payload = SanitizeReadBack(latest)
                    }, cancellationToken: cancellationToken);
                    return new ReadBackVerification(true, attempts, latest, latestReadOrderId);
                }
                await audit.AppendAsync("live_readback_attempt", "completed", "mismatch", details: new
                {
                    attempt = attempts,
                    item_count = latest.Count,
                    observed_payload = SanitizeReadBack(latest)
                }, cancellationToken: cancellationToken);
                break;
            }
            if (DateTimeOffset.UtcNow >= readOrderDeadline) break;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        while (true);

        if (DateTimeOffset.UtcNow >= deadline) break;
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
    while (true);

    await audit.AppendAsync("live_readback_verification", "completed", "mismatch", details: new
    {
        attempts,
        item_count = latest.Count,
        observed_payload = SanitizeReadBack(latest)
    }, cancellationToken: cancellationToken);
    return new ReadBackVerification(false, attempts, latest, latestReadOrderId);
}

static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

static async Task<OrderVerification> TryWaitForOrderAsync(
    DeyeTouCommandAdapter adapter,
    string orderId,
    TimeSpan timeout,
    CancellationToken cancellationToken,
    SanitizedAuditTrail audit)
{
    try
    {
        var result = await adapter.WaitForResultAsync(orderId, timeout, cancellationToken);
        var state = result.Success && result.Status == 666 ? "success" : "failed";
        await audit.AppendAsync("order_verification", "completed", state, details: new
        {
            result_success = result.Success,
            result_status = result.Status
        }, cancellationToken: cancellationToken);
        return new OrderVerification(state);
    }
    catch (TimeoutException)
    {
        await audit.AppendAsync("order_verification", "completed", "timeout", cancellationToken: cancellationToken);
        return new OrderVerification("timeout");
    }
    catch (InvalidOperationException)
    {
        await audit.AppendAsync("order_verification", "completed", "failed", details: new
        {
            exception_type = nameof(InvalidOperationException)
        }, cancellationToken: cancellationToken);
        return new OrderVerification("failed");
    }
}

static async Task<PreflightResult> ReadPreflightAsync(
    HttpClient http,
    IReadOnlyDictionary<string, string> env,
    string masterSerial,
    int freshnessLimitSeconds,
    CancellationToken cancellationToken,
    SanitizedAuditTrail audit)
{
    var stationIdText = Required(env, "DeyeCloud__StationId");
    object stationId = long.TryParse(stationIdText, out var numericStationId) ? numericStationId : stationIdText;
    var devicesRoot = await PostObjectAsync(http, "station/device", new { stationIds = new[] { stationId }, page = 1, size = 20 }, cancellationToken, audit);
    var devices = (devicesRoot["deviceListItems"] ?? devicesRoot["data"]?["deviceListItems"])?.AsArray()
        ?? throw new InvalidOperationException("Deye station/device returned no device list.");
    var inverters = devices
        .Where(x => string.Equals(x?["deviceType"]?.ToString(), "INVERTER", StringComparison.OrdinalIgnoreCase))
        .Select(x => x!.AsObject())
        .ToArray();
    if (inverters.Length != 2) return PreflightResult.Failed;
    if (inverters.Count(x => string.Equals(x["deviceSn"]?.ToString(), masterSerial, StringComparison.Ordinal)) != 1)
        return PreflightResult.Failed;

    var serials = inverters.Select(x => x["deviceSn"]?.ToString() ?? string.Empty).ToArray();
    var latestRoot = await PostObjectAsync(http, "device/latest", new { deviceList = serials }, cancellationToken, audit);
    var latestRows = (latestRoot["deviceDataList"] ?? latestRoot["data"])?.AsArray()
        ?? throw new InvalidOperationException("Deye device/latest returned no telemetry list.");
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var ages = new List<long>(2);
    var bothOnline = true;
    var allFresh = true;
    var noAlarm = true;
    double? masterSoc = null;
    double? masterVoltage = null;

    foreach (var inverter in inverters)
    {
        var serial = inverter["deviceSn"]?.ToString() ?? string.Empty;
        var onlineText = inverter["connectStatus"]?.ToString() ?? inverter["deviceState"]?.ToString() ?? string.Empty;
        bothOnline &= onlineText is "1" or "true" or "True";
        var row = latestRows.FirstOrDefault(x => string.Equals(x?["deviceSn"]?.ToString(), serial, StringComparison.Ordinal))?.AsObject();
        if (row is null) return PreflightResult.Failed;
        var collectionTime = ReadLong(row["collectionTime"]) ?? ReadLong(inverter["collectionTime"]) ?? 0;
        var age = collectionTime > 0 ? now - collectionTime : long.MaxValue;
        ages.Add(age);
        allFresh &= age >= 0 && age <= freshnessLimitSeconds;
        var points = ReadPointMap(row);
        noAlarm &= !HasActiveAlarm(points);
        if (string.Equals(serial, masterSerial, StringComparison.Ordinal))
        {
            masterSoc = ReadDouble(points, "BMSSOC");
            masterVoltage = ReadDouble(points, "BMSVoltage");
        }
    }

    var touRoot = await PostObjectAsync(http, "config/tou", new { deviceSn = masterSerial }, cancellationToken, audit);
    var supportsEnableSell = touRoot["timeUseSettingItems"] is JsonArray touItems &&
        touItems.OfType<JsonObject>().Any(item => item.ContainsKey("enableSell"));
    await PostObjectAsync(http, "config/system", new { deviceSn = masterSerial }, cancellationToken, audit);
    var pass = bothOnline && allFresh && noAlarm && masterSoc is >= 30 && masterVoltage is not null;
    return new PreflightResult(pass, bothOnline, bothOnline && allFresh, allFresh, masterSoc ?? -1, supportsEnableSell, ages.ToArray());
}

static async Task<JsonObject> PostObjectAsync(HttpClient http, string path, object body, CancellationToken cancellationToken, SanitizedAuditTrail audit)
{
    var root = await PostRawObjectAsync(http, path, body, cancellationToken);
    var code = root["code"]?.ToString();
    var accepted = root["success"]?.GetValue<bool>() != false && code is null or "0" or "1000000";
    await audit.AppendAsync("api_operation", "completed", accepted ? "accepted" : "rejected", path,
        details: new { response_code = code, success = root["success"]?.GetValue<bool>() },
        cancellationToken: cancellationToken);
    if (root["success"]?.GetValue<bool>() == false || code is not (null or "0" or "1000000"))
        throw new InvalidOperationException($"Deye {path} returned code {code ?? "unknown"}: {root["msg"]?.ToString() ?? "no message"}.");
    return root;
}

static async Task<JsonObject> PostRawObjectAsync(HttpClient http, string path, object body, CancellationToken cancellationToken)
{
    using var response = await http.PostAsJsonAsync(path, body, cancellationToken);
    response.EnsureSuccessStatusCode();
    var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?.AsObject()
        ?? throw new InvalidOperationException($"Deye {path} response is invalid.");
    return root;
}

static Dictionary<string, string> ReadPointMap(JsonObject row)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (row["dataList"] is not JsonArray points) return result;
    foreach (var pointNode in points)
    {
        if (pointNode is not JsonObject point) continue;
        var key = point["key"]?.ToString();
        if (!string.IsNullOrWhiteSpace(key)) result[key] = point["value"]?.ToString() ?? string.Empty;
    }
    return result;
}

static long? ReadLong(JsonNode? value) => long.TryParse(value?.ToString(), out var result) ? result : null;

static double? ReadDouble(IReadOnlyDictionary<string, string> points, string key) =>
    points.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
        ? result
        : null;

static bool HasActiveAlarm(IReadOnlyDictionary<string, string> points)
{
    foreach (var point in points.Where(x =>
        x.Key.Contains("alarm", StringComparison.OrdinalIgnoreCase) ||
        x.Key.Contains("fault", StringComparison.OrdinalIgnoreCase) ||
        x.Key.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        x.Key.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
        x.Key.Contains("outage", StringComparison.OrdinalIgnoreCase)))
    {
        var value = point.Value.Trim();
        if (value.Length > 0 && value is not ("0" or "0.0") &&
            !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("no", StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
}

static bool Compare(JsonArray actual, IReadOnlyList<DeyeTouInterval> expected, bool compareEnableSell)
{
    if (actual.Count != expected.Count) return false;
    for (var i = 0; i < expected.Count; i++)
    {
        var item = actual[i]?.AsObject();
        if (item is null) return false;
        if (NormalizeTime(item["time"]?.ToString() ?? string.Empty) != NormalizeTime(expected[i].Time)) return false;
        if (!bool.TryParse(item["enableGeneration"]?.ToString(), out var generation) || generation != expected[i].EnableGeneration) return false;
        if (!bool.TryParse(item["enableGridCharge"]?.ToString(), out var grid) || grid != expected[i].EnableGridCharge) return false;
        if (compareEnableSell && (!bool.TryParse(item["enableSell"]?.ToString(), out var sell) || sell != expected[i].EnableSell)) return false;
        if (!DeyeTouCommandAdapter.PowerMatches(GetReadBackInt(item, "power"), expected[i].Power) ||
            GetReadBackInt(item, "soc") != expected[i].Soc ||
            GetReadBackInt(item, "voltage") != expected[i].Voltage) return false;
    }
    return true;
}

static int GetReadBackInt(JsonObject obj, string key) =>
    int.TryParse(obj[key]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : int.MinValue;

static object[] SanitizeReadBack(JsonArray items) => items
    .OfType<JsonObject>()
    .Select(item => (object)new
    {
        time = NormalizeTime(item["time"]?.ToString() ?? string.Empty),
        enableGeneration = item["enableGeneration"]?.ToString(),
        enableGridCharge = item["enableGridCharge"]?.ToString(),
        enableSell = item["enableSell"]?.ToString(),
        power = GetReadBackInt(item, "power"),
        soc = GetReadBackInt(item, "soc"),
        voltage = GetReadBackInt(item, "voltage")
    })
    .ToArray();

internal sealed record ReadBackVerification(bool Matches, int Attempts, JsonArray Items, string ReadOrderId);
internal sealed record OrderVerification(string State);
internal sealed record PreflightResult(bool Pass, bool BothOnline, bool TopologyHealthy, bool AllTelemetryFresh, double MasterBmsSoc, bool SupportsEnableSell, long[] TelemetryAges)
{
    public static PreflightResult Failed { get; } = new(false, false, false, false, -1, false, []);
}
