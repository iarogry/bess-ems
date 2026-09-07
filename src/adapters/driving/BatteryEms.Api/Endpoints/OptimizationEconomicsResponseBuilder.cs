using BatteryEms.Api.Contracts;
using BatteryEms.Application.Optimization;
using BatteryEms.Domain;

namespace BatteryEms.Api.Endpoints;

internal static class OptimizationEconomicsResponseBuilder
{
    public static ScheduleEconomicsView? Build(
        ScheduleOptimizationCommand command,
        Schedule? activeSchedule,
        int? producedScheduleVersion)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.PricesPerStep is null
            || string.IsNullOrWhiteSpace(command.PriceUnit)
            || activeSchedule is null
            || producedScheduleVersion is null
            || activeSchedule.Version != producedScheduleVersion.Value)
        {
            return null;
        }

        var report = ScheduleEconomicsCalculator.Calculate(
            activeSchedule,
            command.PricesPerStep,
            command.PriceUnit,
            command.Asset);
        return ScheduleEconomicsView.From(report);
    }
}
