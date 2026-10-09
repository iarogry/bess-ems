using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Site;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BatteryEms.Adapters.Askue;

public sealed partial class AskueSiteConsumptionCollector
{
    private const string SourceName = "askue";
    private static readonly int[] ProfileIds = [1, 2, 3, 4];
    private static readonly string[] ProfileKeys = ["apoz", "aneg", "ppoz", "pneg"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AskueOptions _options;
    private readonly ISiteConsumptionStore _store;
    private readonly ILogger<AskueSiteConsumptionCollector> _logger;

    public AskueSiteConsumptionCollector(
        IHttpClientFactory httpClientFactory,
        IOptions<AskueOptions> options,
        ISiteConsumptionStore store,
        ILogger<AskueSiteConsumptionCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _store = store;
        _logger = logger;
    }

    public async Task<int> CollectAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(_options.HttpClientName);
        client.DefaultRequestHeaders.Authorization = CreateAuthorizationHeader();

        var points = await GetPointsAsync(client, cancellationToken).ConfigureAwait(false);
        var selected = SelectPoints(points);
        var imported = 0;
        foreach (var point in selected)
        {
            var readings = await FetchReadingsForPointAsync(client, point, date, cancellationToken).ConfigureAwait(false);
            if (readings.Count == 0)
            {
                LogNoData(point.Id, date);
                continue;
            }

            await _store.AppendAsync(readings, cancellationToken).ConfigureAwait(false);
            imported += readings.Count;
            LogPointImported(point.Id, readings.Count, date);
        }

        return imported;
    }

    private static async Task<IReadOnlyList<AskuePoint>> GetPointsAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            new Uri("points", UriKind.Relative),
            content,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("ASKUE points response is not a JSON array.");
            }

            return document.RootElement.EnumerateArray().Select(ParsePoint).ToArray();
        }
    }

    private AskuePoint[] SelectPoints(IReadOnlyList<AskuePoint> points)
    {
        var distinctPoints = points
            .GroupBy(point => point.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var requested = _options.ParsedPointIds;
        if (requested.Count == 0)
        {
            return distinctPoints;
        }

        var requestedSet = requested.ToHashSet(StringComparer.Ordinal);
        if (requestedSet.Except(distinctPoints.Select(point => point.Id), StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("Configured ASKUE point IDs are not accessible for this account.");
        }
        return distinctPoints.Where(point => requestedSet.Contains(point.Id)).ToArray();
    }

    private async Task<IReadOnlyList<SiteConsumptionReading>> FetchReadingsForPointAsync(
        HttpClient client,
        AskuePoint point,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var bounds = GetLocalDayBounds(date, ResolveTimeZone(_options.TimeZoneId));
        var dayStartSeconds = bounds.Start.ToUnixTimeSeconds();
        var dayEndSeconds = bounds.End.ToUnixTimeSeconds();
        var buckets = new SortedDictionary<ConsumptionBucketKey, ConsumptionBucket>();

        for (var i = 0; i < ProfileIds.Length; i++)
        {
            var profile = await FetchProfileAsync(
                client,
                point.Id,
                ProfileIds[i],
                dayStartSeconds,
                dayEndSeconds,
                cancellationToken).ConfigureAwait(false);
            MergeProfile(buckets, profile, ProfileKeys[i]);
        }

        var metadata = JsonSerializer.Serialize(new { scale = point.Scale }, JsonOptions);
        return buckets.Values
            .Where(bucket => !bucket.IsEmpty)
            .OrderBy(bucket => bucket.Timestamp)
            .Select(bucket => bucket.ToReading(_options.SiteId, point.Id, point.Name, metadata))
            .ToArray();
    }

    private static async Task<IReadOnlyList<AskueProfilePoint>> FetchProfileAsync(
        HttpClient client,
        string pointId,
        int profileId,
        long dayStartSeconds,
        long dayEndSeconds,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            new Uri($"point/{Uri.EscapeDataString(pointId)}/profile/{profileId}?b={dayStartSeconds.ToString(CultureInfo.InvariantCulture)}&e={dayEndSeconds.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative),
            content,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var profile = await response.Content
            .ReadFromJsonAsync<IReadOnlyList<AskueProfilePoint>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return profile ?? [];
    }

    private static void MergeProfile(
        SortedDictionary<ConsumptionBucketKey, ConsumptionBucket> buckets,
        IReadOnlyList<AskueProfilePoint> profile,
        string key)
    {
        foreach (var item in profile)
        {
            var bucketKey = new ConsumptionBucketKey(item.Date, item.Step);
            if (!buckets.TryGetValue(bucketKey, out var bucket))
            {
                bucket = new ConsumptionBucket(
                    DateTimeOffset.FromUnixTimeSeconds(item.Date),
                    item.Step);
                buckets[bucketKey] = bucket;
            }

            bucket.Add(key, item.Value);
        }
    }

    private AuthenticationHeaderValue CreateAuthorizationHeader()
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    private static AskuePoint ParsePoint(JsonElement element)
    {
        var id = ReadNamedProperty(element, "id");
        var name = ReadNamedProperty(element, "name");
        var rawScale = ReadNamedProperty(element, "scale");

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("ASKUE point item does not contain identifiable id/name fields.");
        }

        var scale = 1.0;
        if (!string.IsNullOrWhiteSpace(rawScale) && double.TryParse(rawScale, CultureInfo.InvariantCulture, out var parsedScale))
        {
            scale = parsedScale;
        }

        return new AskuePoint(id, name, scale);
    }

    private static string? ReadNamedProperty(JsonElement element, string namePart)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => property.Value.ToString(),
            };
        }

        return null;
    }

    internal static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        var candidates = string.Equals(timeZoneId, "Europe/Kyiv", StringComparison.Ordinal)
            ? new[] { "Europe/Kyiv", "Europe/Kiev", "FLE Standard Time" }
            : string.Equals(timeZoneId, "Europe/Kiev", StringComparison.Ordinal)
                ? new[] { "Europe/Kiev", "Europe/Kyiv", "FLE Standard Time" }
                : string.Equals(timeZoneId, "FLE Standard Time", StringComparison.Ordinal)
                    ? new[] { "FLE Standard Time", "Europe/Kyiv", "Europe/Kiev" }
                    : new[] { timeZoneId };

        foreach (var candidate in candidates)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        if (string.Equals(timeZoneId, "Europe/Kyiv", StringComparison.Ordinal)
            || string.Equals(timeZoneId, "Europe/Kiev", StringComparison.Ordinal)
            || string.Equals(timeZoneId, "FLE Standard Time", StringComparison.Ordinal))
        {
            return CreateKyivFallbackTimeZone();
        }

        throw new TimeZoneNotFoundException($"The time zone ID '{timeZoneId}' was not found on the local computer.");
    }

    private static TimeZoneInfo CreateKyivFallbackTimeZone()
    {
        var daylightDelta = TimeSpan.FromHours(1);
        var dstStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            timeOfDay: new DateTime(1, 1, 1, 3, 0, 0),
            month: 3,
            week: 5,
            dayOfWeek: DayOfWeek.Sunday);
        var dstEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            timeOfDay: new DateTime(1, 1, 1, 4, 0, 0),
            month: 10,
            week: 5,
            dayOfWeek: DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            dateStart: DateTime.MinValue.Date,
            dateEnd: DateTime.MaxValue.Date,
            daylightDelta: daylightDelta,
            daylightTransitionStart: dstStart,
            daylightTransitionEnd: dstEnd);

        return TimeZoneInfo.CreateCustomTimeZone(
            id: "Europe/Kyiv",
            baseUtcOffset: TimeSpan.FromHours(2),
            displayName: "(UTC+02:00) Kyiv",
            standardDisplayName: "EET",
            daylightDisplayName: "EEST",
            adjustmentRules: [rule]);
    }

    internal static (DateTimeOffset Start, DateTimeOffset End) GetLocalDayBounds(
        DateOnly date,
        TimeZoneInfo timeZone)
    {
        var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);
        return (
            new DateTimeOffset(localStart, timeZone.GetUtcOffset(localStart)),
            new DateTimeOffset(localEnd, timeZone.GetUtcOffset(localEnd)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ASKUE imported {Count} consumption readings for point '{PointId}' on {Date}.")]
    private partial void LogPointImported(string pointId, int count, DateOnly date);

    [LoggerMessage(Level = LogLevel.Information, Message = "ASKUE returned no consumption readings for point '{PointId}' on {Date}.")]
    private partial void LogNoData(string pointId, DateOnly date);

    private sealed class ConsumptionBucket
    {
        public ConsumptionBucket(DateTimeOffset timestamp)
            : this(timestamp, null)
        {
        }

        public ConsumptionBucket(DateTimeOffset timestamp, int? intervalSeconds)
        {
            Timestamp = timestamp;
            IntervalSeconds = intervalSeconds;
        }

        public DateTimeOffset Timestamp { get; }
        public int? IntervalSeconds { get; }
        public double? Apoz { get; private set; }
        public double? Aneg { get; private set; }
        public double? Ppoz { get; private set; }
        public double? Pneg { get; private set; }
        public bool IsEmpty => Apoz is null && Aneg is null && Ppoz is null && Pneg is null;

        public void Add(string key, double value)
        {
            var energy = ConvertSourceValueToKiloUnitEnergy(value);
            switch (key)
            {
                case "apoz":
                    Apoz = (Apoz ?? 0) + energy;
                    break;
                case "aneg":
                    Aneg = (Aneg ?? 0) + energy;
                    break;
                case "ppoz":
                    Ppoz = (Ppoz ?? 0) + energy;
                    break;
                case "pneg":
                    Pneg = (Pneg ?? 0) + energy;
                    break;
            }
        }

        private static double ConvertSourceValueToKiloUnitEnergy(double value) => value;

        public SiteConsumptionReading ToReading(string siteId, string pointId, string pointName, string? metadataJson = null) => new(
            siteId,
            pointId,
            pointName,
            Timestamp,
            Apoz,
            Aneg,
            Ppoz,
            Pneg,
            SourceName,
            IntervalSeconds,
            metadataJson);
    }

    private sealed record ConsumptionBucketKey(long TimestampSeconds, int StepSeconds) : IComparable<ConsumptionBucketKey>
    {
        public int CompareTo(ConsumptionBucketKey? other)
        {
            if (other is null)
            {
                return 1;
            }

            var timestampCompare = TimestampSeconds.CompareTo(other.TimestampSeconds);
            return timestampCompare != 0
                ? timestampCompare
                : StepSeconds.CompareTo(other.StepSeconds);
        }
    }
}
