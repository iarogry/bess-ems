using BatteryEms.Adapters.Optimization.OrTools;
using BatteryEms.Adapters.Optimization.Oree;
using BatteryEms.Application.Markets;
using BatteryEms.Application.Optimization;
using BatteryEms.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using ExcelDataReader;
using System.Text;

var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "FLE Standard Time" : "Europe/Kyiv");
var today = args.Length > 0 ? DateTime.ParseExact(args[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).Date : TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
var start = new DateTimeOffset(today, zone.GetUtcOffset(today));
var end = start.AddDays(1);
var request = new PriceSeriesRequest("UA-IPS", "DAM", "day-ahead", "OREE", start, end, TimeSpan.FromHours(1));
Console.WriteLine($"local_start={start:O}; local_end={end:O}");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
using (var probe = new HttpClient())
using (var form = new FormUrlEncodedContent(new Dictionary<string,string>{{"price_date", "09.2026"},{"market_type", "DAM"},{"zone", "IPS"}}))
using (var response = await probe.PostAsync("https://www.oree.com.ua/index.php/pricectr/get_file", form))
using (var stream = await response.Content.ReadAsStreamAsync())
using (var reader = ExcelReaderFactory.CreateBinaryReader(stream))
{
    do { var row = 0; while (row++ < 18 && reader.Read()) Console.WriteLine("ROW|" + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i)) ?? ""))); } while (reader.NextResult());
}
var prices = await new OreePriceFileSource(new HttpClient()).LoadAsync(request, CancellationToken.None);
// Two parallel inverters, each reporting two 300-Ah BMS strings at ~534.6 V.
// Aggregate the four strings into one dispatchable BESS.
var asset = new BatteryAsset("BESS-parallel", 4 * 534.6 * 300 / 1000, 160, 160, 20, 90, .95, .95, 160, -20, 55);
var command = new ScheduleOptimizationCommand(asset.AssetId, ScheduleType.DayAhead, asset, start, end, TimeSpan.FromHours(1), prices.Values, prices.Unit);
var result = await new OrToolsScheduleOptimizer(new ScheduleSolverOptions { InitialSocPercent = 52 }, new Clock(), NullLogger<OrToolsScheduleOptimizer>.Instance).OptimizeAsync(new ScheduleOptimizationRequest(command, "UA-IPS", 0), CancellationToken.None);
Console.WriteLine($"date={today:yyyy-MM-dd}; soc_start=52%; capacity_kwh={asset.CapacityKwh:F2}; prices={prices.StepCount}; status={result.Run.Status}; objective={result.Run.ObjectiveValue:F2}");
Console.WriteLine("prices=" + string.Join(",", prices.Values.Select((p, i) => $"{i:D2}:{p:F2}")));
if (result.ProducedSchedule is not null)
{
    var hourly = result.ProducedSchedule.Windows.ToDictionary(w => TimeZoneInfo.ConvertTime(w.Start, zone).Hour, w => w.TargetPowerKw);
    foreach (var w in result.ProducedSchedule.Windows)
    {
        var localStart = TimeZoneInfo.ConvertTime(w.Start, zone);
        var localEnd = TimeZoneInfo.ConvertTime(w.End, zone);
        Console.WriteLine($"{localStart:HH:mm}-{localEnd:HH:mm}: {w.TargetPowerKw:F1} kW");
    }

    var zones = new[]
    {
        (Name: "Z1 00:00-06:00", Ranges: new[] { (0, 1, false), (1, 2, false), (2, 4, false), (4, 6, false), (6, 12, true), (12, 24, true) }),
        (Name: "Z2 06:00-12:00", Ranges: new[] { (0, 6, true), (6, 7, false), (7, 8, false), (8, 9, false), (9, 12, false), (12, 24, true) }),
        (Name: "Z3 12:00-18:00", Ranges: new[] { (0, 12, true), (12, 13, false), (13, 14, false), (14, 16, false), (16, 18, false), (18, 24, true) }),
        (Name: "Z4 18:00-24:00", Ranges: new[] { (0, 18, true), (18, 19, false), (19, 20, false), (20, 22, false), (22, 23, false), (23, 24, false) })
    };
    foreach (var zonePlan in zones)
    {
        Console.WriteLine($"TOU_ZONE|{zonePlan.Name}|slots={zonePlan.Ranges.Length}");
        foreach (var (from, to, gray) in zonePlan.Ranges)
        {
            var values = Enumerable.Range(from, to - from).Select(h => hourly[h]).ToArray();
            var value = values.Length == 0 ? 0 : values.Average();
            Console.WriteLine($"TOU_SLOT|{from:00}:00-{to:00}:00|{(gray ? "GRAY" : $"{value:F1} kW")}");
        }
    }
}
sealed class Clock : BatteryEms.Application.Time.IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
