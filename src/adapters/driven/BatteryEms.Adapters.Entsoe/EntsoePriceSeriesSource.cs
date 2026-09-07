using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using BatteryEms.Application.Markets;

namespace BatteryEms.Adapters.Entsoe;

public sealed class EntsoePriceSeriesSource : IPriceSeriesSource
{
    public const string SourceId = "entso-e";
    public const string DayAheadProduct = "day_ahead";
    public const string RdnProduct = "rdn";
    public const string EnergyPriceKind = "energy_price";
    public const string EuroPerMwhUnit = "EUR/MWh";

    private const string DocumentTypeDayAheadPrices = "A44";

    private readonly HttpClient _httpClient;
    private readonly EntsoePriceSeriesOptions _options;
    private readonly IPriceSeriesImportSink? _importSink;

    public EntsoePriceSeriesSource(
        HttpClient httpClient,
        EntsoePriceSeriesOptions options,
        IPriceSeriesImportSink? importSink = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _options = options.EnsureValid();
        _importSink = importSink;
        _httpClient.Timeout = _options.RequestTimeout;
    }

    public async Task<PriceSeries> LoadAsync(
        PriceSeriesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();
        EnsureSupportedRequest(request);

        using var response = await _httpClient.GetAsync(
            BuildRequestUri(request), cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound || string.IsNullOrWhiteSpace(payload))
        {
            throw new PriceSeriesNotFoundException(request);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"entsoe-request-failed status={(int)response.StatusCode} reason={response.ReasonPhrase}");
        }

        var parsed = ParseDayAheadPrices(payload, request);
        var series = new PriceSeries(
            request.MarketBidArea,
            _options.Product,
            _options.PriceKind,
            parsed.Unit ?? _options.Unit,
            _options.Source,
            request.HorizonStart,
            request.HorizonEnd,
            request.TimeStep,
            parsed.Values);
        if (_importSink is not null)
        {
            await _importSink.ImportAsync(series, cancellationToken).ConfigureAwait(false);
        }

        return series;
    }

    internal static EntsoeParsedPriceSeries ParseDayAheadPrices(
        string xmlPayload,
        PriceSeriesRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlPayload);
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();

        var document = XDocument.Parse(xmlPayload);
        var pricesByTimestamp = new Dictionary<DateTimeOffset, double>();
        string? unit = null;
        foreach (var timeSeries in document.Root?.ElementsByLocalName("TimeSeries") ?? [])
        {
            unit ??= TryBuildUnit(timeSeries);
            var period = timeSeries.ElementsByLocalName("Period").FirstOrDefault();
            if (period is null)
            {
                continue;
            }

            var periodStart = ParseUtcTimestamp(
                RequiredText(period.ElementByPath("timeInterval", "start"), "timeInterval/start"));
            var resolution = ParseResolution(
                period.ElementsByLocalName("resolution").FirstOrDefault()?.Value,
                request.TimeStep);
            if (resolution != request.TimeStep)
            {
                throw new InvalidOperationException(
                    $"entsoe-resolution-mismatch source={resolution} requested={request.TimeStep}");
            }

            var points = period.ElementsByLocalName("Point")
                .Select(point => new PricePoint(
                    Position: int.Parse(
                        RequiredText(point.ElementsByLocalName("position").FirstOrDefault(), "position"),
                        CultureInfo.InvariantCulture),
                    Price: double.Parse(
                        RequiredText(point.ElementsByLocalName("price.amount").FirstOrDefault(), "price.amount"),
                        CultureInfo.InvariantCulture)))
                .OrderBy(point => point.Position)
                .ToArray();
            var duplicatePosition = points
                .GroupBy(point => point.Position)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicatePosition is not null)
            {
                throw new InvalidOperationException(
                    $"entsoe-duplicate-position position={duplicatePosition.Key}");
            }

            double? activePrice = null;
            var nextPointIndex = 0;
            var periodStepCount = checked((int)((ParseUtcTimestamp(
                RequiredText(period.ElementByPath("timeInterval", "end"), "timeInterval/end")) - periodStart).Ticks
                / resolution.Ticks));
            for (var position = 1; position <= periodStepCount; position++)
            {
                if (nextPointIndex < points.Length && points[nextPointIndex].Position == position)
                {
                    activePrice = points[nextPointIndex].Price;
                    nextPointIndex++;
                }
                if (activePrice is null)
                {
                    continue;
                }

                var timestamp = periodStart + TimeSpan.FromTicks(resolution.Ticks * (position - 1));
                if (timestamp < request.HorizonStart || timestamp >= request.HorizonEnd)
                {
                    continue;
                }
                if (!pricesByTimestamp.TryAdd(timestamp, activePrice.Value))
                {
                    throw new InvalidOperationException($"entsoe-duplicate-price timestamp={timestamp:O}");
                }
            }
        }

        var stepCount = checked((int)((request.HorizonEnd - request.HorizonStart).Ticks / request.TimeStep.Ticks));
        var values = new double[stepCount];
        for (var i = 0; i < values.Length; i++)
        {
            var timestamp = request.HorizonStart + TimeSpan.FromTicks(request.TimeStep.Ticks * i);
            if (!pricesByTimestamp.TryGetValue(timestamp, out var value))
            {
                throw new InvalidOperationException($"entsoe-price-gap timestamp={timestamp:O}");
            }

            values[i] = value;
        }

        return new EntsoeParsedPriceSeries(Array.AsReadOnly(values), unit);
    }

    private static string? TryBuildUnit(XElement timeSeries)
    {
        var currency = timeSeries.ElementsByLocalName("currency_Unit.name")
            .FirstOrDefault()?.Value.Trim();
        var measure = timeSeries.ElementsByLocalName("price_Measure_Unit.name")
            .FirstOrDefault()?.Value.Trim();
        if (string.IsNullOrWhiteSpace(currency) || string.IsNullOrWhiteSpace(measure))
        {
            return null;
        }

        return $"{currency}/{NormalizeMeasureUnit(measure)}";
    }

    private static string NormalizeMeasureUnit(string value) =>
        string.Equals(value, "MWH", StringComparison.OrdinalIgnoreCase)
            ? "MWh"
            : value;

    private Uri BuildRequestUri(PriceSeriesRequest request)
    {
        var parameters = new Dictionary<string, string>
        {
            ["securityToken"] = _options.SecurityToken,
            ["documentType"] = DocumentTypeDayAheadPrices,
            ["in_Domain"] = _options.DomainCode,
            ["out_Domain"] = _options.DomainCode,
            ["periodStart"] = request.HorizonStart.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture),
            ["periodEnd"] = request.HorizonEnd.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture),
        };
        var builder = new UriBuilder(_options.BaseUrl)
        {
            Query = string.Join("&", parameters.Select(
                pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    private void EnsureSupportedRequest(PriceSeriesRequest request)
    {
        if (!string.Equals(request.Source, _options.Source, StringComparison.OrdinalIgnoreCase))
        {
            throw new PriceSeriesNotFoundException(request);
        }
        if (!string.Equals(request.Product, _options.Product, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Product, RdnProduct, StringComparison.OrdinalIgnoreCase))
        {
            throw new PriceSeriesNotFoundException(request);
        }
        if (!string.Equals(request.PriceKind, _options.PriceKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new PriceSeriesNotFoundException(request);
        }
        if (request.TimeStep != TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                $"entsoe-rdn-time-step-unsupported requested={request.TimeStep}; supported=01:00:00");
        }
    }

    private static DateTimeOffset ParseUtcTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static TimeSpan ParseResolution(string? value, TimeSpan fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : XmlConvert.ToTimeSpan(value);

    private static string RequiredText(XElement? element, string name) =>
        element?.Value.Trim()
        ?? throw new InvalidOperationException($"entsoe-schema-missing field={name}");
}

internal sealed record EntsoeParsedPriceSeries(IReadOnlyList<double> Values, string? Unit);

internal sealed record PricePoint(int Position, double Price);

internal static class EntsoeXmlExtensions
{
    public static IEnumerable<XElement> ElementsByLocalName(this XElement element, string localName) =>
        element.Elements().Where(child => string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal));

    public static XElement? ElementByPath(this XElement element, params string[] localNames)
    {
        var current = element;
        foreach (var localName in localNames)
        {
            current = current.ElementsByLocalName(localName).FirstOrDefault();
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}
