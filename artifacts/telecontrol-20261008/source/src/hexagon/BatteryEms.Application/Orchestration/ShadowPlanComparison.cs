namespace BatteryEms.Application.Orchestration;

public sealed record ShadowTouInterval(
    string StartTime,
    bool EnableGeneration,
    bool EnableGridCharge,
    bool EnableSell,
    int PowerWatts,
    int SocPercent,
    int Voltage);

public sealed record ShadowTouWindow(
    string WindowId,
    IReadOnlyList<ShadowTouInterval> Intervals);

public sealed record ShadowPlanSnapshot(
    string SiteId,
    DateOnly DeliveryDate,
    bool PayloadReady,
    IReadOnlyList<string> BlockingCodes,
    IReadOnlyList<ShadowTouWindow> Windows)
{
    public ShadowPlanSnapshot EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentNullException.ThrowIfNull(BlockingCodes);
        ArgumentNullException.ThrowIfNull(Windows);

        if (PayloadReady && BlockingCodes.Count != 0)
        {
            throw new ArgumentException(
                "An executable shadow plan cannot contain blocking codes.",
                nameof(BlockingCodes));
        }

        if (!PayloadReady && Windows.Count != 0)
        {
            throw new ArgumentException(
                "A blocked shadow plan cannot contain executable windows.",
                nameof(Windows));
        }

        if (PayloadReady && Windows.Count != 4)
        {
            throw new ArgumentException(
                "An executable Deye shadow plan must contain exactly four windows.",
                nameof(Windows));
        }

        foreach (var window in Windows)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(window.WindowId);
            if (window.Intervals.Count != 6)
            {
                throw new ArgumentException(
                    $"Window {window.WindowId} must contain exactly six intervals.",
                    nameof(Windows));
            }

            ValidateStarts(window);
        }

        return this;
    }

    private static void ValidateStarts(ShadowTouWindow window)
    {
        var previous = TimeSpan.MinValue;
        foreach (var interval in window.Intervals)
        {
            if (!TimeSpan.TryParseExact(
                    interval.StartTime,
                    "hh\\:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var current)
                || current <= previous)
            {
                throw new ArgumentException(
                    $"Window {window.WindowId} has invalid or unordered start times.",
                    nameof(window));
            }

            previous = current;
        }

        if (previous == TimeSpan.MinValue
            || window.Intervals[0].StartTime != "00:00")
        {
            throw new ArgumentException(
                $"Window {window.WindowId} must start at 00:00.",
                nameof(window));
        }
    }
}

public sealed record ShadowPlanMismatch(string Path, string LegacyValue, string ShadowValue);

public sealed record ShadowPlanComparisonResult(IReadOnlyList<ShadowPlanMismatch> Mismatches)
{
    public bool IsEquivalent => Mismatches.Count == 0;
}

/// <summary>
/// Exact, side-effect-free comparison used while the legacy workflow remains
/// authoritative. A mismatch is evidence for investigation, never permission
/// to select either plan automatically.
/// </summary>
public static class ShadowPlanComparer
{
    public static ShadowPlanComparisonResult Compare(
        ShadowPlanSnapshot legacy,
        ShadowPlanSnapshot shadow)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(shadow);
        legacy = legacy.EnsureValid();
        shadow = shadow.EnsureValid();

        var mismatches = new List<ShadowPlanMismatch>();
        AddIfDifferent(mismatches, "site_id", legacy.SiteId, shadow.SiteId);
        AddIfDifferent(
            mismatches,
            "delivery_date",
            legacy.DeliveryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            shadow.DeliveryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        AddIfDifferent(
            mismatches,
            "payload_ready",
            legacy.PayloadReady.ToString(),
            shadow.PayloadReady.ToString());

        CompareBlockingCodes(legacy.BlockingCodes, shadow.BlockingCodes, mismatches);
        CompareWindows(legacy.Windows, shadow.Windows, mismatches);
        return new ShadowPlanComparisonResult(mismatches);
    }

    private static void CompareBlockingCodes(
        IReadOnlyList<string> legacy,
        IReadOnlyList<string> shadow,
        ICollection<ShadowPlanMismatch> mismatches)
    {
        AddIfDifferent(
            mismatches,
            "blocking_codes",
            string.Join(",", legacy.Order(StringComparer.Ordinal)),
            string.Join(",", shadow.Order(StringComparer.Ordinal)));
    }

    private static void CompareWindows(
        IReadOnlyList<ShadowTouWindow> legacy,
        IReadOnlyList<ShadowTouWindow> shadow,
        ICollection<ShadowPlanMismatch> mismatches)
    {
        AddIfDifferent(
            mismatches,
            "windows.count",
            legacy.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            shadow.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var windowIndex = 0; windowIndex < Math.Min(legacy.Count, shadow.Count); windowIndex++)
        {
            var leftWindow = legacy[windowIndex];
            var rightWindow = shadow[windowIndex];
            var windowPath = $"windows[{windowIndex}]";
            AddIfDifferent(mismatches, $"{windowPath}.id", leftWindow.WindowId, rightWindow.WindowId);

            for (var intervalIndex = 0; intervalIndex < 6; intervalIndex++)
            {
                var left = leftWindow.Intervals[intervalIndex];
                var right = rightWindow.Intervals[intervalIndex];
                var path = $"{windowPath}.intervals[{intervalIndex}]";
                AddIfDifferent(mismatches, $"{path}.start_time", left.StartTime, right.StartTime);
                AddIfDifferent(mismatches, $"{path}.enable_generation", left.EnableGeneration.ToString(), right.EnableGeneration.ToString());
                AddIfDifferent(mismatches, $"{path}.enable_grid_charge", left.EnableGridCharge.ToString(), right.EnableGridCharge.ToString());
                AddIfDifferent(mismatches, $"{path}.enable_sell", left.EnableSell.ToString(), right.EnableSell.ToString());
                AddIfDifferent(mismatches, $"{path}.power_watts", Format(left.PowerWatts), Format(right.PowerWatts));
                AddIfDifferent(mismatches, $"{path}.soc_percent", Format(left.SocPercent), Format(right.SocPercent));
                AddIfDifferent(mismatches, $"{path}.voltage", Format(left.Voltage), Format(right.Voltage));
            }
        }
    }

    private static void AddIfDifferent(
        ICollection<ShadowPlanMismatch> mismatches,
        string path,
        string legacy,
        string shadow)
    {
        if (!string.Equals(legacy, shadow, StringComparison.Ordinal))
        {
            mismatches.Add(new ShadowPlanMismatch(path, legacy, shadow));
        }
    }

    private static string Format(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
