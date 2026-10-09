using System.Globalization;
using System.Text.Json;
using BatteryEms.Application.Orchestration;

namespace BatteryEms.Adapters.Optimization.Deye;

/// <summary>
/// Read-only adapter for the scenario artifact consumed by the established
/// switching runner. It maps only the bounded comparison fields and never
/// returns raw JSON, vendor identifiers or credentials.
/// </summary>
public sealed class LegacyDeyeScenarioFileSource : ILegacyPlanSnapshotSource
{
    private const long MaximumScenarioBytes = 8 * 1024 * 1024;

    private readonly string _scenarioDirectory;

    public LegacyDeyeScenarioFileSource(string scenarioDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioDirectory);
        _scenarioDirectory = Path.GetFullPath(scenarioDirectory);
    }

    public async Task<LegacyPlanSnapshotLoadResult> LoadAsync(
        string siteId,
        DateOnly deliveryDate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        var dateText = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var executablePath = Path.Combine(_scenarioDirectory, $"deye-tou-{dateText}.json");
        var blockedPath = Path.Combine(_scenarioDirectory, $"deye-tou-{dateText}.blocked.json");
        var path = File.Exists(executablePath)
            ? executablePath
            : File.Exists(blockedPath) ? blockedPath : null;
        if (path is null)
        {
            return new LegacyPlanSnapshotLoadResult(
                null,
                "legacy-scenario-missing",
                "No legacy scenario artifact exists for the delivery date.");
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumScenarioBytes)
            {
                return Invalid("Legacy scenario size is outside the accepted range.");
            }

            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return Parse(document.RootElement, siteId, deliveryDate);
            }
        }
        catch (JsonException)
        {
            return Invalid("Legacy scenario JSON is malformed.");
        }
        catch (IOException)
        {
            return new LegacyPlanSnapshotLoadResult(
                null,
                "legacy-scenario-read-failed",
                "Legacy scenario could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return new LegacyPlanSnapshotLoadResult(
                null,
                "legacy-scenario-read-failed",
                "Legacy scenario could not be read.");
        }
    }

    private static LegacyPlanSnapshotLoadResult Parse(
        JsonElement root,
        string siteId,
        DateOnly requestedDate)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryReadDate(root, out var artifactDate)
            || artifactDate != requestedDate
            || !root.TryGetProperty("payloadReady", out var readyElement)
            || readyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Invalid("Legacy scenario identity or payloadReady is invalid.");
        }

        try
        {
            var payloadReady = readyElement.GetBoolean();
            var snapshot = payloadReady
                ? new ShadowPlanSnapshot(
                    siteId,
                    requestedDate,
                    true,
                    [],
                    ReadWindows(root))
                : new ShadowPlanSnapshot(
                    siteId,
                    requestedDate,
                    false,
                    ReadBlockingCodes(root),
                    []);
            return new LegacyPlanSnapshotLoadResult(snapshot.EnsureValid());
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or FormatException
                or OverflowException)
        {
            return Invalid("Legacy scenario structure is invalid.");
        }
    }

    private static ShadowTouWindow[] ReadWindows(JsonElement root)
    {
        var windows = RequiredArray(root, "touWindows");
        return windows.EnumerateArray()
            .Select(window => new ShadowTouWindow(
                RequiredString(window, "id"),
                RequiredArray(window, "intervals")
                    .EnumerateArray()
                    .Select(ReadInterval)
                    .ToArray()))
            .ToArray();
    }

    private static ShadowTouInterval ReadInterval(JsonElement interval) =>
        new(
            RequiredString(interval, "time"),
            RequiredBoolean(interval, "enableGeneration"),
            RequiredBoolean(interval, "enableGridCharge"),
            RequiredBoolean(interval, "enableSell"),
            RequiredInt32(interval, "power"),
            RequiredInt32(interval, "soc"),
            RequiredInt32(interval, "voltage"));

    private static string[] ReadBlockingCodes(JsonElement root)
    {
        var codes = RequiredArray(root, "blockedReasons")
            .EnumerateArray()
            .Select(reason => RequiredString(reason, "code"))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (codes.Length == 0)
        {
            throw new InvalidDataException("A blocked scenario requires a reason code.");
        }

        return codes;
    }

    private static bool TryReadDate(JsonElement root, out DateOnly date)
    {
        date = default;
        var property = root.TryGetProperty("deliveryDate", out var delivery)
            ? delivery
            : root.TryGetProperty("target_date", out var target) ? target : default;
        return property.ValueKind == JsonValueKind.String
               && DateOnly.TryParseExact(
                   property.GetString(),
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out date);
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Required array {name} is missing.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Required string {name} is missing.");
        }

        return value.GetString()!;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Required boolean {name} is missing.");
        }

        return value.GetBoolean();
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"Required integer {name} is missing.");
        }

        return result;
    }

    private static LegacyPlanSnapshotLoadResult Invalid(string message) =>
        new(null, "legacy-scenario-invalid", message);
}
