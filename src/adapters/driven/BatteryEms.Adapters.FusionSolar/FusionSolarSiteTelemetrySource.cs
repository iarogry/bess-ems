using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using BatteryEms.Application.Realtime;
using BatteryEms.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.FusionSolar;

public sealed partial class FusionSolarSiteTelemetrySource : IDisposable
{
    private const string HttpClientName = "FusionSolar";
    private const string XsrfCookieName = "XSRF-TOKEN";
    private const string ActivePowerKey = "active_power";
    private const string InverterYieldKey = "inverterYield";
    private const string InverterPowerKey = "inverter_power";
    private const string DayPowerKey = "day_power";
    private const string PvYieldKey = "PVYield";
    private static readonly TimeSpan TokenReusePeriod = TimeSpan.FromMinutes(29);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FusionSolarOptions _options;
    private readonly ISiteTelemetryStore _siteTelemetry;
    private readonly ILogger<FusionSolarSiteTelemetrySource> _logger;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private string? _xsrfToken;
    private DateTimeOffset _xsrfTokenExpiresAt;

    public FusionSolarSiteTelemetrySource(
        IHttpClientFactory httpClientFactory,
        IOptions<FusionSolarOptions> options,
        ISiteTelemetryStore siteTelemetry,
        ILogger<FusionSolarSiteTelemetrySource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(siteTelemetry);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _siteTelemetry = siteTelemetry;
        _logger = logger;
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
            readings = await ReadRealTimeStationTelemetryAsync(
                client,
                token,
                stationCodes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogStationPollFailed(ex, stationList);
            return 0;
        }
        catch (JsonException ex)
        {
            LogStationPollFailed(ex, stationList);
            return 0;
        }
        catch (TaskCanceledException ex)
        {
            LogStationPollFailed(ex, stationList);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            LogStationPollFailed(ex, stationList);
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
            .Where(item => item.PvPowerKw is not null)
            .ToArray();

        foreach (var missingStation in stationCodes.Where(code =>
                     usable.All(item => !string.Equals(
                         item.Reading.StationCode,
                         code,
                         StringComparison.OrdinalIgnoreCase))))
        {
            LogNoStationData(missingStation);
        }

        if (usable.Length == 0)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(_options.AssetId))
        {
            var totalPvPowerKw = usable.Sum(item => item.PvPowerKw!.Value);
            var telemetry = new SiteTelemetry(
                Timestamp: now,
                AssetId: _options.AssetId,
                PvPowerKw: totalPvPowerKw,
                LoadPowerKw: null,
                GridPowerKw: null,
                IrradianceWPerSquareMeter: null,
                DataQuality: DataQuality.Valid);
            _siteTelemetry.Update(telemetry, now);
            LogStationUpdated(stationList, telemetry.AssetId, telemetry.Timestamp, telemetry.PvPowerKw);
            return usable.Length;
        }

        foreach (var item in usable)
        {
            var timestamp = item.Reading.CollectTime is null
                ? now
                : DateTimeOffset.FromUnixTimeMilliseconds(item.Reading.CollectTime.Value);
            var telemetry = CreateTelemetry(item.Reading.StationCode, timestamp, item.Reading.DataItemMap);
            _siteTelemetry.Update(telemetry, now);
            LogStationUpdated(item.Reading.StationCode, telemetry.AssetId, telemetry.Timestamp, telemetry.PvPowerKw);
        }

        return usable.Length;
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

    private SiteTelemetry CreateTelemetry(
        string stationCode,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, double?> dataItemMap)
    {
        var assetId = ResolveAssetId(stationCode);
        return new SiteTelemetry(
            Timestamp: timestamp,
            AssetId: assetId,
            PvPowerKw: ResolvePvPowerKw(dataItemMap),
            LoadPowerKw: null,
            GridPowerKw: null,
            IrradianceWPerSquareMeter: null,
            DataQuality: DataQuality.Valid);
    }

    private static double? ResolvePvPowerKw(IReadOnlyDictionary<string, double?> dataItemMap)
    {
        foreach (var key in new[] { ActivePowerKey, InverterPowerKey, DayPowerKey, InverterYieldKey, PvYieldKey })
        {
            if (dataItemMap.TryGetValue(key, out var value) && value is not null)
            {
                return value;
            }
        }

        return null;
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

    private string ResolveAssetId(string stationCode) =>
        string.IsNullOrWhiteSpace(_options.AssetId)
            ? stationCode
            : _options.AssetId;

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "FusionSolar returned no usable hourly KPI data for station '{StationCode}'.")]
    private partial void LogNoStationData(string stationCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FusionSolar failed to poll station '{StationCode}'. Continuing with remaining stations.")]
    private partial void LogStationPollFailed(Exception exception, string stationCode);
}
