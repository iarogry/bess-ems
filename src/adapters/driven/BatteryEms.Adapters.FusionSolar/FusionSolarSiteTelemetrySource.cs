using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using BatteryEms.Application.Realtime;
using BatteryEms.Application.Site;
using BatteryEms.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.FusionSolar;

public sealed partial class FusionSolarSiteTelemetrySource : IDisposable
{
    private const string HttpClientName = "FusionSolar";
    private const string XsrfCookieName = "XSRF-TOKEN";
    private const string ActivePowerKey = "active_power";
    private static readonly TimeSpan TokenReusePeriod = TimeSpan.FromMinutes(29);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FusionSolarOptions _options;
    private readonly ISiteTelemetryStore _siteTelemetry;
    private readonly ISiteMeasurementStore? _measurements;
    private readonly ILogger<FusionSolarSiteTelemetrySource> _logger;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private string? _xsrfToken;
    private DateTimeOffset _xsrfTokenExpiresAt;
    private JsonElement _deviceList;
    private DateTimeOffset _deviceListExpiresAt;

    public FusionSolarSiteTelemetrySource(
        IHttpClientFactory httpClientFactory,
        IOptions<FusionSolarOptions> options,
        ISiteTelemetryStore siteTelemetry,
        ILogger<FusionSolarSiteTelemetrySource> logger,
        ISiteMeasurementStore? measurements = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(siteTelemetry);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _siteTelemetry = siteTelemetry;
        _logger = logger;
        _measurements = measurements;
    }

    public Task<int> PollAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        PollStationsAsync(_options.ParsedStationCodes, now, cancellationToken);

    public Task<int> PollStationAsync(
        string stationCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stationCode))
        {
            throw new ArgumentException("FusionSolar station code is required.", nameof(stationCode));
        }

        if (!_options.ParsedStationCodes.Contains(stationCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"FusionSolar station '{stationCode}' is not configured.",
                nameof(stationCode));
        }

        return PollStationsAsync([stationCode], now, cancellationToken);
    }

    private async Task<int> PollStationsAsync(
        IReadOnlyList<string> stationCodes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stationList = string.Join(',', stationCodes);
        IReadOnlyList<FusionSolarStationKpi> readings;
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var token = await GetValidTokenAsync(client, now, cancellationToken).ConfigureAwait(false);
            readings = _options.UseDeviceTelemetry
                ? await ReadDeviceTelemetryAsync(client, token, stationCodes, now, cancellationToken).ConfigureAwait(false)
                : await ReadRealTimeStationTelemetryAsync(
                client,
                token,
                stationCodes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            LogStationPollFailed(ex, stationList);
            await PersistAsync(stationCodes.Select(code => new SiteTelemetry(now, code,
                null, null, null, null, DataQuality.ProtocolError("fusionsolar-source-error"))), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            _requestGate.Release();
        }

        var usable = readings
            .Where(reading => stationCodes.Contains(reading.StationCode, StringComparer.OrdinalIgnoreCase))
            .GroupBy(reading => reading.StationCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(reading => reading.CollectTime).First())
            .Select(reading => new
            {
                Reading = reading,
                PvPowerKw = ResolvePvPowerKw(reading.DataItemMap),
            })
            .ToArray();

        foreach (var missingStation in stationCodes.Where(code =>
                     !usable.Any(item => string.Equals(
                         item.Reading.StationCode,
                         code,
                         StringComparison.OrdinalIgnoreCase) && item.PvPowerKw is not null)))
        {
            LogNoStationData(missingStation);
        }

        var snapshots = new Dictionary<string, SiteTelemetry>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in stationCodes)
        {
            var item = usable.SingleOrDefault(value => string.Equals(value.Reading.StationCode, code, StringComparison.OrdinalIgnoreCase));
            var timestamp = ResolveTimestamp(item?.Reading.CollectTime, now);
            var quality = item?.PvPowerKw is null
                ? DataQuality.ProtocolError("fusionsolar-active-power-missing-or-invalid")
                : timestamp is null ? DataQuality.ProtocolError("fusionsolar-measurement-time-missing-or-invalid")
                : now - timestamp.Value > TimeSpan.FromSeconds(_options.MaxMeasurementAgeSeconds)
                    ? DataQuality.Stale("fusionsolar-measurement-aged")
                : item.Reading.IsTimestampSubstituted
                    ? DataQuality.Substituted("fusionsolar-device-sample-time-unavailable-observed-at-poll-time") : DataQuality.Valid;
            var telemetry = new SiteTelemetry(timestamp ?? now, code,
                quality.Flag == DataQualityState.ProtocolError ? null : item?.PvPowerKw,
                null, null, null, quality);
            snapshots.Add(code, telemetry);
            _siteTelemetry.Update(telemetry, now);
            LogStationUpdated(code, telemetry.AssetId, telemetry.Timestamp, telemetry.PvPowerKw);
        }

        if (!string.IsNullOrWhiteSpace(_options.AssetId) && _options.ParsedSiteStationCodes.Count > 0)
        {
            var members = _options.ParsedSiteStationCodes;
            var complete = members.All(code => snapshots.TryGetValue(code, out var value) && value.DataQuality.IsUsableForControl);
            var totalPower = complete ? members.Sum(code => snapshots[code].PvPowerKw!.Value) : (double?)null;
            complete = complete && totalPower is double total && double.IsFinite(total);
            var telemetry = new SiteTelemetry(
                complete ? members.Min(code => snapshots[code].Timestamp) : now,
                _options.AssetId,
                complete ? totalPower : null,
                null, null, null,
                complete ? DataQuality.Valid : DataQuality.ProtocolError("fusionsolar-site-incomplete-or-stale"));
            _siteTelemetry.Update(telemetry, now);
        }

        await PersistAsync(snapshots.Values, cancellationToken).ConfigureAwait(false);
        return snapshots.Values.Count(value => value.PvPowerKw is not null
            && value.DataQuality.Flag is DataQualityState.Valid or DataQualityState.Substituted);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Storage outages must not prevent live telemetry updates; failures are logged.")]
    private async Task PersistAsync(IEnumerable<SiteTelemetry> snapshots, CancellationToken cancellationToken)
    {
        if (_measurements is null) { return; }
        var rows = snapshots.Select(snapshot => new SiteMeasurementReading(
            _options.StationSiteIds.GetValueOrDefault(snapshot.AssetId) ?? $"unassigned:fusionsolar:{snapshot.AssetId}",
            "fusionsolar", "pv", snapshot.AssetId, snapshot.AssetId, snapshot.Timestamp, null,
            "pv_power", snapshot.PvPowerKw, "kW", snapshot.DataQuality.Flag switch
            {
                DataQualityState.Valid => "valid",
                DataQualityState.Substituted => "substituted",
                DataQualityState.Stale => "stale",
                _ => "source_error",
            }, MetadataJson: JsonSerializer.Serialize(new { reason = snapshot.DataQuality.Reason }))).ToArray();
        try
        {
            await _measurements.AppendAsync(rows, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogPersistenceFailed(ex); }
    }

    private async Task<IReadOnlyList<FusionSolarStationKpi>> ReadDeviceTelemetryAsync(
        HttpClient client, string token, IReadOnlyList<string> stationCodes,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Account -> station -> string inverter. Meters/loggers are not PV generators.
        if (_deviceList.ValueKind != JsonValueKind.Array || now >= _deviceListExpiresAt)
        {
            var envelope = await PostEnvelopeAsync(client, "getDevList",
                new FusionSolarStationKpiRequest { StationCodes = string.Join(',', _options.ParsedStationCodes) },
                token, cancellationToken).ConfigureAwait(false);
            if (envelope.Data.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("FusionSolar device list is not an array.");
            }
            _deviceList = envelope.Data.Clone();
            _deviceListExpiresAt = now.AddHours(1);
        }

        var devices = _deviceList.EnumerateArray()
            .Where(device => device.GetProperty("devTypeId").GetInt32() == 1)
            .Select(device => new
            {
                Id = device.GetProperty("id").GetInt64(),
                Station = device.GetProperty("stationCode").GetString() ?? string.Empty,
            })
            .Where(device => stationCodes.Contains(device.Station, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(device => device.Id).ToArray();
        var measurements = new Dictionary<long, JsonElement>();
        foreach (var batch in devices.Chunk(100))
        {
            var envelope = await PostEnvelopeAsync(client, "getDevRealKpi",
                new { devIds = string.Join(',', batch.Select(device => device.Id)), devTypeId = 1 },
                token, cancellationToken).ConfigureAwait(false);
            if (envelope.Data.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("FusionSolar device telemetry is not an array.");
            }
            foreach (var reading in envelope.Data.EnumerateArray())
            {
                var id = reading.GetProperty("devId").GetInt64();
                if (batch.Any(device => device.Id == id))
                {
                    // A duplicate device response is ambiguous; never count it twice.
                    if (!measurements.TryAdd(id, reading.Clone()))
                    {
                        throw new JsonException("FusionSolar returned duplicate inverter telemetry.");
                    }
                }
            }
        }

        var result = new List<FusionSolarStationKpi>();
        foreach (var station in stationCodes)
        {
            var members = devices.Where(device => string.Equals(device.Station, station, StringComparison.OrdinalIgnoreCase)).ToArray();
            var complete = members.Length > 0;
            var power = 0d;
            var oldest = now.ToUnixTimeMilliseconds();
            var substituted = false;
            foreach (var member in members)
            {
                if (!measurements.TryGetValue(member.Id, out var reading)
                    || !reading.GetProperty("dataItemMap").TryGetProperty(ActivePowerKey, out var value)
                    || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var kw) || !double.IsFinite(kw))
                {
                    complete = false;
                    break;
                }
                power += kw;
                if (reading.TryGetProperty("collectTime", out var time) && time.TryGetInt64(out var milliseconds))
                {
                    if (ResolveTimestamp(milliseconds, now) is null) { complete = false; break; }
                    oldest = Math.Min(oldest, milliseconds);
                }
                else
                {
                    // Huawei cloud omits device sample time. Expose observation time explicitly
                    // as substituted, so it cannot qualify an aggregate or control input as Valid.
                    substituted = true;
                }
            }
            result.Add(new FusionSolarStationKpi
            {
                StationCode = station, CollectTime = oldest, IsTimestampSubstituted = substituted,
                DataItemMap = new Dictionary<string, double?>
                {
                    [ActivePowerKey] = complete && double.IsFinite(power) ? power : null,
                },
            });
        }
        return result;
    }

    private async Task<string> GetValidTokenAsync(
        HttpClient client,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_xsrfToken) && now < _xsrfTokenExpiresAt)
        {
            return _xsrfToken;
        }

        using var response = await client.PostAsJsonAsync(
            "login",
            new FusionSolarLoginRequest
            {
                UserName = _options.User,
                SystemCode = _options.Password,
            },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var token = TryExtractXsrfToken(response);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("FusionSolar login did not return XSRF-TOKEN.");
        }

        _xsrfToken = token;
        _xsrfTokenExpiresAt = now.Add(TokenReusePeriod);
        return token;
    }

    private static async Task<IReadOnlyList<FusionSolarStationKpi>> ReadRealTimeStationTelemetryAsync(
        HttpClient client,
        string xsrfToken,
        IReadOnlyList<string> stationCodes,
        CancellationToken cancellationToken)
    {
        var request = new FusionSolarStationKpiRequest
        {
            StationCodes = string.Join(',', stationCodes),
        };
        var envelope = await PostEnvelopeAsync(
            client,
            "getStationRealKpi",
            request,
            xsrfToken,
            cancellationToken).ConfigureAwait(false);
        return EnumerateStations(envelope, JsonOptions).ToArray();
    }

    private static async Task<FusionSolarRawEnvelope> PostEnvelopeAsync(
        HttpClient client,
        string path,
        object request,
        string xsrfToken,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        httpRequest.Headers.TryAddWithoutValidation(XsrfCookieName, xsrfToken);
        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content
            .ReadFromJsonAsync<FusionSolarRawEnvelope>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (envelope is null)
        {
            throw new InvalidOperationException($"FusionSolar returned empty response for '{path}'.");
        }
        if (!envelope.Success)
        {
            throw new FusionSolarApiException(
                path,
                envelope.FailCode,
                envelope.Message,
                envelope.Data.ValueKind == JsonValueKind.String ? envelope.Data.GetString() : envelope.Data.GetRawText());
        }

        return envelope;
    }

    private static double? ResolvePvPowerKw(IReadOnlyDictionary<string, double?> dataItemMap)
    {
        return dataItemMap.TryGetValue(ActivePowerKey, out var value)
            && value is double power && double.IsFinite(power) ? power : null;
    }

    private static DateTimeOffset? ResolveTimestamp(long? milliseconds, DateTimeOffset now)
    {
        if (milliseconds is null or <= 0 || milliseconds > now.ToUnixTimeMilliseconds()) { return null; }
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value);
    }

    private static IEnumerable<FusionSolarStationKpi> EnumerateStations(
        FusionSolarRawEnvelope envelope,
        JsonSerializerOptions options)
    {
        if (TryDeserializeStations(envelope.Data, options, out var direct))
        {
            return direct;
        }

        if (envelope.Data.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in envelope.Data.EnumerateObject())
            {
                if (TryDeserializeStations(property.Value, options, out var nested))
                {
                    return nested;
                }
            }
        }

        throw new JsonException("FusionSolar response field '$.data' did not contain a station KPI array.");
    }

    private static bool TryDeserializeStations(
        JsonElement candidate,
        JsonSerializerOptions options,
        [NotNullWhen(true)] out IReadOnlyList<FusionSolarStationKpi>? stations)
    {
        stations = null;
        if (candidate.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        stations = JsonSerializer.Deserialize<IReadOnlyList<FusionSolarStationKpi>>(
            candidate.GetRawText(),
            options);
        return stations is not null;
    }

    private static string? TryExtractXsrfToken(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries))
            {
                if (!part.StartsWith(XsrfCookieName + "=", StringComparison.Ordinal))
                {
                    continue;
                }

                return part[(XsrfCookieName.Length + 1)..];
            }
        }

        return null;
    }

    public void Dispose() => _requestGate.Dispose();

    [LoggerMessage(Level = LogLevel.Debug, Message = "FusionSolar updated station '{StationCode}' as asset '{AssetId}' at {Timestamp:O}: PvPowerKw={PvPowerKw}.")]
    private partial void LogStationUpdated(string stationCode, string assetId, DateTimeOffset timestamp, double? pvPowerKw);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FusionSolar returned no usable real-time power data for station '{StationCode}'.")]
    private partial void LogNoStationData(string stationCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FusionSolar failed to poll station '{StationCode}'. Continuing with remaining stations.")]
    private partial void LogStationPollFailed(Exception exception, string stationCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "FusionSolar measurement persistence failed; live snapshots remain available.")]
    private partial void LogPersistenceFailed(Exception exception);
}
