using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BatteryEms.Application.IO;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Site;
using BatteryEms.Application.Persistence;
using BatteryEms.Domain;

namespace BatteryEms.Adapters.DeyeCloud;

public sealed partial class DeyeCloudTelemetrySource : IBatteryTelemetrySource, IDisposable
{
    private const string KeyBatteryPower = "batteryPower";
    private const string KeyBatteryCapacity = "batCapcity";
    private const string KeyBatteryVoltage = "batteryVoltage";
    private const string KeyBatteryCurrent = "batteryCurrent";
    private const string KeyGridPower = "totalGridPower";
    private const string KeyPvPower = "totalSolarPower";
    private const string KeyLoadPower = "totalLoadPower";
    private const string KeyIrradiance = "irradiateIntensity";

    private const string HttpClientName = "DeyeCloud";
    private const double WattsToKilowatts = 1_000d;
    private static readonly Regex Sha256HexRegex = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeyeAdapterOptions _options;
    private readonly ILogger<DeyeCloudTelemetrySource> _logger;
    private readonly ISiteTelemetryStore? _siteTelemetry;
    private readonly ISiteMeasurementStore? _measurements;
    private readonly ITelemetryRepository? _telemetryRepository;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    // Справжні поля для трекінгу стану рекорду AdapterStatus
    private DateTimeOffset? _lastSuccessfulRead;
    private string? _lastError;
    private long _consecutiveFailures;

    public AdapterStatus Status => new(
        Connected: _cachedToken != null && _consecutiveFailures == 0,
        LastSuccessfulReadAt: _lastSuccessfulRead,
        LastError: _lastError,
        ConsecutiveFailures: _consecutiveFailures
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling              = JsonNumberHandling.AllowReadingFromString
    };

    public DeyeCloudTelemetrySource(
        IHttpClientFactory httpClientFactory,
        IOptions<DeyeAdapterOptions> options,
        ILogger<DeyeCloudTelemetrySource> logger,
        ISiteTelemetryStore? siteTelemetry = null,
        ISiteMeasurementStore? measurements = null,
        ITelemetryRepository? telemetryRepository = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options           = options?.Value     ?? throw new ArgumentNullException(nameof(options));
        _logger            = logger             ?? throw new ArgumentNullException(nameof(logger));
        _siteTelemetry     = siteTelemetry;
        _measurements = measurements;
        _telemetryRepository = telemetryRepository;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Infrastructure gateway must translate any cloud crash into an explicit ProtocolError to safely trigger BESS safe mode.")]
    public async IAsyncEnumerable<BatteryTelemetry> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var identifier = !string.IsNullOrWhiteSpace(_options.StationId) ? _options.StationId : _options.DeviceSn ?? "UNKNOWN";
        var assetId = ResolveAssetId();
        LogStartingPolling(identifier);

        while (!cancellationToken.IsCancellationRequested)
        {
            SiteTelemetry? siteReading = null;
            Dictionary<string, double?>? batteryValues = null;
            BatteryTelemetry telemetry;

            try
            {
                await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);

                IReadOnlyList<DataPoint>? dataList = null;
                if (!string.IsNullOrWhiteSpace(_options.StationId))
                {
                    LogFetchingStationData(_options.StationId);
                    var stationResponse = await PostEnvelopeAsync<StationLatestRequest, DeviceLatestData>(
                        path: "station/latest",
                        body: new StationLatestRequest { StationId = _options.StationId },
                        requiresAuth: true,
                        ct: cancellationToken).ConfigureAwait(false);

                    dataList = stationResponse.Data?.DataList;
                    if ((dataList is null || dataList.Count == 0) && TryCreateStationRootDataList(stationResponse, out var rootDataList))
                    {
                        dataList = rootDataList;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(_options.DeviceSn))
                {
                    LogFetchingLatestData(_options.DeviceSn);

                    var latestResponse = await PostJsonAsync<DeviceLatestRequest, IReadOnlyList<DeviceLatestData>>(
                        path: "device/latest",
                        body: new DeviceLatestRequest { DeviceList = [_options.DeviceSn] },
                        requiresAuth: true,
                        ct: cancellationToken).ConfigureAwait(false);

                    var deviceData = latestResponse?.FirstOrDefault(
                        d => string.Equals(d.DeviceSn, _options.DeviceSn, StringComparison.OrdinalIgnoreCase));
                    
                    dataList = deviceData?.DataList;
                }

                if (dataList is null || dataList.Count == 0)
                {
                    LogNoDataForDevice(assetId);
                    _consecutiveFailures++;
                    _lastError = "Empty data metrics array inside cloud envelope";
                    telemetry = CreateInvalidTelemetry(_lastError);
                }
                else
                {
                    TryGetDouble(dataList, KeyBatteryPower, out double batteryPowerW);
                    TryGetDouble(dataList, KeyBatteryCapacity, out double soc);
                    TryGetDouble(dataList, KeyBatteryVoltage, out double voltageV);
                    TryGetDouble(dataList, KeyBatteryCurrent, out double currentA);
                    var gridPowerW = TryGetOptionalDouble(dataList, KeyGridPower);
                    var pvPowerW = TryGetOptionalDouble(dataList, KeyPvPower);
                    var loadPowerW = TryGetOptionalDouble(dataList, KeyLoadPower);
                    var irradiance = TryGetOptionalDouble(dataList, KeyIrradiance);
                    batteryValues = new Dictionary<string, double?>
                    {
                        ["battery_power"] = ToKilowatts(TryGetOptionalDouble(dataList, KeyBatteryPower)),
                        ["soc"] = TryGetOptionalDouble(dataList, KeyBatteryCapacity),
                        ["dc_voltage"] = TryGetOptionalDouble(dataList, KeyBatteryVoltage),
                        ["dc_current"] = TryGetOptionalDouble(dataList, KeyBatteryCurrent),
                    };

                    double activeKw = batteryPowerW switch
                    {
                        > 0d => Math.Abs(batteryPowerW) / WattsToKilowatts,
                        < 0d => -Math.Abs(batteryPowerW) / WattsToKilowatts,
                        _ => 0d
                    };

                    _consecutiveFailures = 0;
                    _lastError = null;
                    _lastSuccessfulRead = DateTimeOffset.UtcNow;
                    UpdateSiteTelemetry(assetId, pvPowerW, loadPowerW, gridPowerW, irradiance, _lastSuccessfulRead.Value);
                    siteReading = new SiteTelemetry(_lastSuccessfulRead.Value, assetId,
                        ToKilowatts(pvPowerW), ToKilowatts(loadPowerW), ToKilowatts(gridPowerW), irradiance, DataQuality.Valid);

                    telemetry = new BatteryTelemetry(
                        Timestamp: DateTimeOffset.UtcNow,
                        AssetId: assetId,
                        SocPercent: soc,
                        SohPercent: 100.0,
                        ActivePowerKw: activeKw,
                        ReactivePowerKvar: 0d,
                        DcVoltage: voltageV,
                        DcCurrent: currentA,
                        TemperatureCelsius: 25.0,
                        Available: true,
                        FaultStatus: "OK",
                        DataQuality: DataQuality.Valid
                    );

                    LogTelemetrySuccess(assetId, activeKw, soc);
                }
            }
            catch (Exception ex)
            {
                LogTelemetryError(identifier, ex);
                _consecutiveFailures++;
                _lastError = ex.Message;
                telemetry = CreateInvalidTelemetry($"API Error: {ex.Message}");
            }

            await PersistAsync(telemetry, siteReading, batteryValues, cancellationToken).ConfigureAwait(false);
            yield return telemetry;

            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Storage outages must not stop the live telemetry iterator; failures are logged.")]
    private async Task PersistAsync(BatteryTelemetry battery, SiteTelemetry? site,
        IReadOnlyDictionary<string, double?>? batteryValues, CancellationToken cancellationToken)
    {
        const string timeReason = "deye-sample-time-unavailable-observed-at-poll-time";
        if (_telemetryRepository is not null)
        {
            try
            {
                await _telemetryRepository.AppendAsync(battery with
                {
                    DataQuality = PersistentBatteryQuality(battery, batteryValues),
                    FaultStatus = battery.Available ? battery.FaultStatus : "deye-source-error",
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { LogPersistenceFailed(ex); }
        }
        if (_measurements is null) { return; }
        var id = !string.IsNullOrWhiteSpace(_options.StationId) ? _options.StationId : _options.DeviceSn ?? battery.AssetId;
        var siteId = string.IsNullOrWhiteSpace(_options.SiteId) ? $"unassigned:deye_cloud:{id}" : _options.SiteId;
        var rows = new List<SiteMeasurementReading>();
        void Add(string type, string metric, double? value, string unit)
        {
            rows.Add(new SiteMeasurementReading(siteId, "deye_cloud", type, id, id,
                battery.Timestamp, null, metric, value is double number && double.IsFinite(number) ? number : null,
                unit, value is double finite && double.IsFinite(finite) ? "substituted" : "missing",
                MetadataJson: JsonSerializer.Serialize(new { asset_id = battery.AssetId, reason = timeReason })));
        }
        Add("pv", "pv_power", site?.PvPowerKw, "kW");
        Add("load", "load_power", site?.LoadPowerKw, "kW");
        Add("grid", "grid_power", site?.GridPowerKw, "kW");
        Add("pv", "irradiance", site?.IrradianceWPerSquareMeter, "W/m2");
        Add("battery", "battery_power", battery.Available ? batteryValues?.GetValueOrDefault("battery_power") : null, "kW");
        Add("battery", "battery_soc", battery.Available ? batteryValues?.GetValueOrDefault("soc") : null, "%");
        Add("battery", "dc_voltage", battery.Available ? batteryValues?.GetValueOrDefault("dc_voltage") : null, "V");
        Add("battery", "dc_current", battery.Available ? batteryValues?.GetValueOrDefault("dc_current") : null, "A");
        if (!battery.Available)
        {
            rows = rows.Select(row => row with { Quality = "source_error", MetadataJson = "{\"reason\":\"deye-source-error\"}" }).ToList();
        }
        try { await _measurements.AppendAsync(rows, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogPersistenceFailed(ex); }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Deye Cloud telemetry persistence failed; live polling continues.")]
    private partial void LogPersistenceFailed(Exception exception);

    private static DataQuality PersistentBatteryQuality(BatteryTelemetry battery, IReadOnlyDictionary<string, double?>? values)
    {
        if (!battery.Available) { return DataQuality.ProtocolError("deye-source-error"); }
        var reason = "deye-sample-time-unavailable-observed-at-poll-time;soh-reactive-power-temperature-defaulted";
        if (values?.Values.Any(value => value is null) == true) { reason += ";battery-fields-missing-defaulted-to-zero"; }
        return DataQuality.Substituted(reason);
    }

    private BatteryTelemetry CreateInvalidTelemetry(string reason)
    {
        return new BatteryTelemetry(
            Timestamp: DateTimeOffset.UtcNow,
            AssetId: ResolveAssetId(),
            SocPercent: 0,
            SohPercent: 0,
            ActivePowerKw: 0,
            ReactivePowerKvar: 0,
            DcVoltage: 0,
            DcCurrent: 0,
            TemperatureCelsius: 0,
            Available: false,
            FaultStatus: reason,
            DataQuality: DataQuality.ProtocolError(reason)
        );
    }

    private string ResolveAssetId() =>
        !string.IsNullOrWhiteSpace(_options.AssetId)
            ? _options.AssetId
            : !string.IsNullOrWhiteSpace(_options.StationId)
                ? _options.StationId
                : _options.DeviceSn ?? "UNKNOWN";

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        var leadTime = TimeSpan.FromSeconds(_options.TokenRefreshLeadSeconds);
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry - leadTime)
            return;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry - leadTime)
                return;

            LogRefreshingToken();

            var request = new TokenRequest
            {
                AppSecret  = _options.AppSecret,
                Email      = _options.Email,
                Password   = NormalizePasswordForApi(_options.Password, _options.PasswordIsSha256),
                CompanyId  = _options.CompanyId
            };

            var path = $"/account/token?appId={Uri.EscapeDataString(_options.AppId)}";

            var tokenResponse = await PostEnvelopeAsync<TokenRequest, TokenData>(
                path: path,
                body: request,
                requiresAuth: false,
                ct: ct).ConfigureAwait(false);
            var tokenData = tokenResponse.Data;
            var token = tokenData?.Token;
            if (string.IsNullOrWhiteSpace(token))
            {
                token = tokenResponse.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Deye Cloud returned an empty token.");

            _cachedToken = token;
            _tokenExpiry = ResolveTokenExpiry(tokenData?.ExpiresIn ?? tokenResponse.ExpiresIn);

            LogTokenRefreshed(_tokenExpiry);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        bool requiresAuth,
        CancellationToken ct)
    {
        var envelope = await PostEnvelopeAsync<TRequest, TResponse>(
            path, body, requiresAuth, ct).ConfigureAwait(false);
        return envelope.Data;
    }

    private async Task<DeyeApiResponse<TResponse>> PostEnvelopeAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        bool requiresAuth,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // КУЛЕЗАХИСТ: Сами зшиваємо абсолютний URL. 
        // Жодні зайві або забуті слеші в конфігах більше нічого не зламають!
        var baseUrl = client.BaseAddress?.ToString() ?? _options.BaseUrl;
        var fullUrl = $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

        for (int attempt = 0; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            try
            {
                // ПЕРЕДАЄМО fullUrl ЗАМІСТЬ ОРИГІНАЛЬНОГО path
                using var request = new HttpRequestMessage(HttpMethod.Post, fullUrl);
                request.Content = JsonContent.Create(body, options: JsonOptions);

                if (requiresAuth && _cachedToken is not null)
                    request.Headers.TryAddWithoutValidation("Authorization", $"bearer {_cachedToken}");

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                var envelope = await response.Content
                    .ReadFromJsonAsync<DeyeApiResponse<TResponse>>(JsonOptions, ct)
                    .ConfigureAwait(false);

                if (envelope is null)
                    throw new InvalidOperationException($"Null envelope for path '{path}'.");

                if (!envelope.IsSuccess)
                    throw new InvalidOperationException($"API error on '{path}': code={envelope.Code}, msg='{envelope.Msg}'.");

                return envelope;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                        && !ct.IsCancellationRequested
                                        && attempt < _options.MaxRetryAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(_options.RetryBaseDelayMs * Math.Pow(2, attempt));
                LogRetryAttempt(path, attempt + 1, _options.MaxRetryAttempts, (int)delay.TotalMilliseconds, ex);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"All retry attempts exhausted for path '{path}'.");
    }
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Deye Cloud OpenAPI strictly mandates lowercase hex representation for SHA-256 signatures")]
    private static string HashPassword(string plainPassword)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainPassword));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Deye Cloud OpenAPI strictly mandates lowercase hex representation for SHA-256 signatures")]
    private static string NormalizePasswordForApi(string password, bool passwordIsSha256)
    {
        if (passwordIsSha256 || Sha256HexRegex.IsMatch(password))
        {
            return password.ToLowerInvariant();
        }

        return HashPassword(password);
    }

    private static DateTimeOffset ResolveTokenExpiry(long? expiresIn)
    {
        var now = DateTimeOffset.UtcNow;
        if (expiresIn is null)
        {
            return now.AddHours(1);
        }
        if (expiresIn > now.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds(expiresIn.Value);
        }

        return now.AddSeconds(Math.Max(0, expiresIn.Value));
    }

    private static bool TryCreateStationRootDataList(
        DeyeApiResponse<DeviceLatestData> response,
        out IReadOnlyList<DataPoint> dataList)
    {
        var points = new List<DataPoint>();
        AddPoint(points, KeyBatteryPower, response.BatteryPower, "W");
        AddPoint(points, KeyBatteryCapacity, response.BatterySoc, "%");
        AddPoint(points, KeyGridPower, response.GridPower, "W");
        AddPoint(points, KeyPvPower, response.GenerationPower, "W");
        AddPoint(points, KeyLoadPower, response.ConsumptionPower, "W");
        AddPoint(points, KeyIrradiance, response.IrradiateIntensity, "W/m2");
        dataList = points;
        return points.Count > 0;
    }

    private void UpdateSiteTelemetry(
        string assetId,
        double? pvPowerW,
        double? loadPowerW,
        double? gridPowerW,
        double? irradiance,
        DateTimeOffset timestamp)
    {
        _siteTelemetry?.Update(
            new SiteTelemetry(
                Timestamp: timestamp,
                AssetId: assetId,
                PvPowerKw: ToKilowatts(pvPowerW),
                LoadPowerKw: ToKilowatts(loadPowerW),
                GridPowerKw: ToKilowatts(gridPowerW),
                IrradianceWPerSquareMeter: irradiance,
                DataQuality: DataQuality.Valid),
            timestamp);
    }

    private static void AddPoint(List<DataPoint> points, string key, double? value, string unit)
    {
        if (value is null)
        {
            return;
        }

        points.Add(new DataPoint
        {
            Key = key,
            Value = value.Value.ToString(CultureInfo.InvariantCulture),
            Unit = unit,
        });
    }

    private static bool TryGetDouble(IReadOnlyList<DataPoint> dataList, string key, out double value)
    {
        foreach (var point in dataList)
        {
            if (!string.Equals(point.Key, key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (double.TryParse(point.Value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                return true;

            value = 0d;
            return false;
        }
        value = 0d;
        return false;
    }

    private static double? TryGetOptionalDouble(IReadOnlyList<DataPoint> dataList, string key)
    {
        return TryGetDouble(dataList, key, out var value)
            ? value
            : null;
    }

    private static double? ToKilowatts(double? watts) => watts / WattsToKilowatts;

    public void Dispose()
    {
        _tokenLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Structured log messages (source-generated)

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting Deye Cloud OpenAPI telemetry poll stream for: {Identifier}")]
    private partial void LogStartingPolling(string identifier);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetching latest telemetry from Deye Cloud for device '{AssetId}'.")]
    private partial void LogFetchingLatestData(string assetId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetching latest telemetry from Deye Cloud for station '{StationId}'.")]
    private partial void LogFetchingStationData(string stationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deye Cloud returned no data for '{AssetId}'. Returning Invalid telemetry.")]
    private partial void LogNoDataForDevice(string assetId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Telemetry retrieved successfully for device '{AssetId}': ActivePowerKw={ActivePowerKw:F3}, SoC={Soc:F1}%.")]
    private partial void LogTelemetrySuccess(string assetId, double activePowerKw, double soc);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to retrieve telemetry for device '{AssetId}'. Returning Invalid telemetry to trigger EMS Safe Mode.")]
    private partial void LogTelemetryError(string assetId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deye Cloud bearer token is absent or near expiry — refreshing.")]
    private partial void LogRefreshingToken();

    [LoggerMessage(Level = LogLevel.Information, Message = "Deye Cloud bearer token refreshed successfully. New expiry: {Expiry:O}.")]
    private partial void LogTokenRefreshed(DateTimeOffset expiry);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient error on '{Path}' (attempt {Attempt}/{MaxAttempts}). Retrying in {DelayMs} ms.")]
    private partial void LogRetryAttempt(string path, int attempt, int maxAttempts, int delayMs, Exception ex);

    #endregion
}
