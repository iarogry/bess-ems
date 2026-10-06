using System.Globalization;
using BatteryEms.Application.Markets;
using ExcelDataReader;

namespace BatteryEms.Adapters.Optimization.Oree;

public sealed record OreePriceFileOptions(
    string Endpoint = "https://www.oree.com.ua/index.php/pricectr/get_file",
    string MarketType = "DAM",
    string Zone = "IPS");

/// <summary>Loads the official OREE monthly XLS price export.</summary>
public sealed class OreePriceFileSource
{
    private readonly HttpClient _http;
    private readonly OreePriceFileOptions _options;

    public OreePriceFileSource(HttpClient http, OreePriceFileOptions? options = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new OreePriceFileOptions();
    }

    public async Task<PriceSeries> LoadAsync(PriceSeriesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.EnsureValid();
        if (request.TimeStep != TimeSpan.FromHours(1))
            throw new NotSupportedException("OREE export is hourly; TimeStep must be 1 hour.");

        var kyiv = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "FLE Standard Time" : "Europe/Kyiv");
        var month = TimeZoneInfo.ConvertTime(request.HorizonStart, kyiv)
            .ToString("MM.yyyy", CultureInfo.InvariantCulture);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["price_date"] = month,
            ["market_type"] = _options.MarketType,
            ["zone"] = _options.Zone
        });
        using var response = await _http.PostAsync(new Uri(_options.Endpoint), content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var values = OreeXlsParser.Parse(stream, checked((int)(request.HorizonEnd - request.HorizonStart).TotalHours), TimeZoneInfo.ConvertTime(request.HorizonStart, kyiv).Day);
            return new PriceSeries(request.MarketBidArea, request.Product, request.PriceKind,
                "UAH/MWh", "OREE", request.HorizonStart, request.HorizonEnd, request.TimeStep, values);
        }
    }
}

public static class OreeXlsParser
{
    public static IReadOnlyList<double> Parse(Stream source, int expectedCount, int targetDay)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var workbook = ExcelReaderFactory.CreateBinaryReader(source);
        var rows = new List<double[]>();
        do
        {
            while (workbook.Read())
            {
                if (workbook.FieldCount < expectedCount + 1) continue;
                var dayText = Convert.ToString(workbook.GetValue(0), CultureInfo.InvariantCulture)?.Trim();
                if (!int.TryParse(dayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) || day != targetDay) continue;
                var row = new double[expectedCount]; var valid = true;
                for (var col = 0; col < expectedCount; col++)
                {
                    var text = Convert.ToString(workbook.GetValue(col + 1), CultureInfo.InvariantCulture)?.Trim();
                    if (text is null || (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) || !double.IsFinite(v)) { valid = false; break; }
                    row[col] = v;
                }
                if (valid) rows.Add(row);
            }
        } while (workbook.NextResult());
        if (rows.Count == 0) throw new InvalidDataException($"OREE XLS contains no complete hourly row for day {targetDay}.");
        if (rows.Count > 1) throw new InvalidDataException($"OREE XLS contains duplicate rows for day {targetDay}.");
        return rows[0];
    }
}
