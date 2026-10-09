using System.Net;
using System.Globalization;
using BatteryEms.Adapters.Entsoe;
using BatteryEms.Application.Markets;
using Xunit;

namespace BatteryEms.Adapters.Entsoe.Tests;

public sealed class EntsoePriceSeriesSourceTests
{
    private static readonly double[] ExpandedUkraineRdnPrices =
    [
        4596.42,
        3300.0,
        1980.0,
        1980.0,
        1980.0,
        2127.0,
        4100.0,
        5500.0,
        5900.0,
        4000.0,
        4000.0,
        2789.0,
        2789.0,
        2867.0,
        2789.0,
        1789.0,
        3005.16,
        4414.0,
        6490.0,
        6900.0,
        7600.0,
        8300.0,
        7988.0,
        6440.0,
    ];

    [Fact]
    public async Task LoadAsync_fetches_a44_day_ahead_prices_and_maps_to_price_series()
    {
        var handler = new ScriptedHandler(SamplePublicationDocument());
        using var client = new HttpClient(handler);
        var source = new EntsoePriceSeriesSource(client, Options());
        var request = Request();

        var series = await source.LoadAsync(request, CancellationToken.None);

        Assert.Equal("10Y1001A1001A39I", series.MarketBidArea);
        Assert.Equal("day_ahead", series.Product);
        Assert.Equal("energy_price", series.PriceKind);
        Assert.Equal("EUR/MWh", series.Unit);
        Assert.Equal("entso-e", series.Source);
        Assert.Equal(Utc("2026-06-03T00:00:00Z"), series.HorizonStart);
        Assert.Equal(Utc("2026-06-03T03:00:00Z"), series.HorizonEnd);
        Assert.Equal(TimeSpan.FromHours(1), series.TimeStep);
        Assert.Equal([10.25, -5.5, 42.0], series.Values);

        Assert.NotNull(handler.RequestUri);
        var query = handler.RequestUri!.Query;
        Assert.Contains("documentType=A44", query, StringComparison.Ordinal);
        Assert.Contains("in_Domain=10Y1001A1001A39I", query, StringComparison.Ordinal);
        Assert.Contains("out_Domain=10Y1001A1001A39I", query, StringComparison.Ordinal);
        Assert.Contains("periodStart=202606030000", query, StringComparison.Ordinal);
        Assert.Contains("periodEnd=202606030300", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_rejects_gaps_in_requested_horizon()
    {
        var source = new EntsoePriceSeriesSource(
            new HttpClient(new ScriptedHandler(SamplePublicationDocument())),
            Options());
        var request = Request() with
        {
            HorizonEnd = Utc("2026-06-03T04:00:00Z"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.LoadAsync(request, CancellationToken.None));

        Assert.Contains("entsoe-price-gap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_returns_not_found_for_other_source()
    {
        var source = new EntsoePriceSeriesSource(new HttpClient(new ScriptedHandler("")), Options());
        var request = Request() with { Source = "manual" };

        await Assert.ThrowsAsync<PriceSeriesNotFoundException>(
            () => source.LoadAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_imports_successful_api_series_when_sink_is_configured()
    {
        var sink = new RecordingPriceSeriesSink();
        var source = new EntsoePriceSeriesSource(
            new HttpClient(new ScriptedHandler(SamplePublicationDocument())),
            Options(),
            sink);

        var series = await source.LoadAsync(Request(), CancellationToken.None);

        Assert.Same(series, sink.Imported);
    }

    [Fact]
    public async Task LoadAsync_expands_sparse_ukraine_rdn_positions_and_keeps_local_market_day_offset()
    {
        var handler = new ScriptedHandler(SparseUkraineRdnPublicationDocument());
        using var client = new HttpClient(handler);
        var options = Options();
        options.DomainCode = "10Y1001C--000182";
        var source = new EntsoePriceSeriesSource(client, options);
        var request = new PriceSeriesRequest(
            MarketBidArea: "10Y1001C--000182",
            Product: "rdn",
            PriceKind: "energy_price",
            Source: "entso-e",
            HorizonStart: new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.FromHours(3)),
            HorizonEnd: new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.FromHours(3)),
            TimeStep: TimeSpan.FromHours(1));

        var series = await source.LoadAsync(request, CancellationToken.None);

        Assert.Equal("UAH/MWh", series.Unit);
        Assert.Equal(request.HorizonStart, series.HorizonStart);
        Assert.Equal(request.HorizonEnd, series.HorizonEnd);
        Assert.Equal(ExpandedUkraineRdnPrices, series.Values);
        Assert.Contains("periodStart=202606022100", handler.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("periodEnd=202606032100", handler.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_rejects_another_area_before_requesting_prices()
    {
        using var client = new HttpClient(new ScriptedHandler(SamplePublicationDocument()));
        var source = new EntsoePriceSeriesSource(client, Options());
        await Assert.ThrowsAsync<PriceSeriesNotFoundException>(() => source.LoadAsync(
            Request() with { MarketBidArea = "another-area" }, CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_rejects_missing_currency_instead_of_assuming_euros()
    {
        var xml = SamplePublicationDocument().Replace("<currency_Unit.name>EUR</currency_Unit.name>", "", StringComparison.Ordinal);
        using var client = new HttpClient(new ScriptedHandler(xml));
        var source = new EntsoePriceSeriesSource(client, Options());
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.LoadAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_rejects_mixed_currencies_in_one_response()
    {
        var xml = SamplePublicationDocument().Replace("</Publication_MarketDocument>",
            "<TimeSeries><currency_Unit.name>UAH</currency_Unit.name><price_Measure_Unit.name>MWH</price_Measure_Unit.name></TimeSeries></Publication_MarketDocument>", StringComparison.Ordinal);
        using var client = new HttpClient(new ScriptedHandler(xml));
        var source = new EntsoePriceSeriesSource(client, Options());
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.LoadAsync(Request(), CancellationToken.None));
    }

    private static EntsoePriceSeriesOptions Options() => new()
    {
        BaseUrl = new Uri("https://example.test/entsoe"),
        SecurityToken = "test-token",
        DomainCode = "10Y1001A1001A39I",
    };

    private static PriceSeriesRequest Request() => new(
        MarketBidArea: "10Y1001A1001A39I",
        Product: "day_ahead",
        PriceKind: "energy_price",
        Source: "entso-e",
        HorizonStart: Utc("2026-06-03T00:00:00Z"),
        HorizonEnd: Utc("2026-06-03T03:00:00Z"),
        TimeStep: TimeSpan.FromHours(1));

    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string SamplePublicationDocument() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <Publication_MarketDocument xmlns="urn:iec62325.351:tc57wg16:451-3:publicationdocument:7:3">
          <TimeSeries>
            <currency_Unit.name>EUR</currency_Unit.name>
            <price_Measure_Unit.name>MWH</price_Measure_Unit.name>
            <Period>
              <timeInterval>
                <start>2026-06-03T00:00Z</start>
                <end>2026-06-03T03:00Z</end>
              </timeInterval>
              <resolution>PT60M</resolution>
              <Point>
                <position>1</position>
                <price.amount>10.25</price.amount>
              </Point>
              <Point>
                <position>2</position>
                <price.amount>-5.5</price.amount>
              </Point>
              <Point>
                <position>3</position>
                <price.amount>42.0</price.amount>
              </Point>
            </Period>
          </TimeSeries>
        </Publication_MarketDocument>
        """;

    private static string SparseUkraineRdnPublicationDocument() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <Publication_MarketDocument xmlns="urn:iec62325.351:tc57wg16:451-3:publicationdocument:7:3">
          <TimeSeries>
            <currency_Unit.name>UAH</currency_Unit.name>
            <price_Measure_Unit.name>MWH</price_Measure_Unit.name>
            <Period>
              <timeInterval>
                <start>2026-06-02T21:00Z</start>
                <end>2026-06-03T21:00Z</end>
              </timeInterval>
              <resolution>PT60M</resolution>
              <Point><position>1</position><price.amount>4596.42</price.amount></Point>
              <Point><position>2</position><price.amount>3300.0</price.amount></Point>
              <Point><position>3</position><price.amount>1980.0</price.amount></Point>
              <Point><position>6</position><price.amount>2127.0</price.amount></Point>
              <Point><position>7</position><price.amount>4100.0</price.amount></Point>
              <Point><position>8</position><price.amount>5500.0</price.amount></Point>
              <Point><position>9</position><price.amount>5900.0</price.amount></Point>
              <Point><position>10</position><price.amount>4000.0</price.amount></Point>
              <Point><position>12</position><price.amount>2789.0</price.amount></Point>
              <Point><position>14</position><price.amount>2867.0</price.amount></Point>
              <Point><position>15</position><price.amount>2789.0</price.amount></Point>
              <Point><position>16</position><price.amount>1789.0</price.amount></Point>
              <Point><position>17</position><price.amount>3005.16</price.amount></Point>
              <Point><position>18</position><price.amount>4414.0</price.amount></Point>
              <Point><position>19</position><price.amount>6490.0</price.amount></Point>
              <Point><position>20</position><price.amount>6900.0</price.amount></Point>
              <Point><position>21</position><price.amount>7600.0</price.amount></Point>
              <Point><position>22</position><price.amount>8300.0</price.amount></Point>
              <Point><position>23</position><price.amount>7988.0</price.amount></Point>
              <Point><position>24</position><price.amount>6440.0</price.amount></Point>
            </Period>
          </TimeSeries>
        </Publication_MarketDocument>
        """;

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly string _payload;

        public ScriptedHandler(string payload)
        {
            _payload = payload;
        }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_payload),
            });
        }
    }

    private sealed class RecordingPriceSeriesSink : IPriceSeriesImportSink
    {
        public PriceSeries? Imported { get; private set; }

        public Task ImportAsync(PriceSeries series, CancellationToken cancellationToken)
        {
            Imported = series;
            return Task.CompletedTask;
        }
    }
}
