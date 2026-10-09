using BatteryEms.Application.Realtime;
using BatteryEms.Application.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class ChpTelemetryEndpoints
{
    public static IEndpointRouteBuilder MapChpTelemetry(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/chp/{assetId}/status", (string assetId, IChpTelemetryStore store, IClock clock) =>
        {
            var telemetry = store.GetLatest(assetId, clock.UtcNow, TimeSpan.FromMinutes(2));
            return telemetry is null ? Results.NotFound() : Results.Ok(telemetry);
        }).WithName("ChpTelemetryStatus").WithSummary("Read-only CHP power, parameters, message codes and measurement quality.");
        return routes;
    }
}
