using BatteryEms.Api.Contracts;
using BatteryEms.Application.Api;
using BatteryEms.Application.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class SiteTelemetryEndpoints
{
    public static IEndpointRouteBuilder MapSiteTelemetryStatus(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/site/{assetId}/status", async (
                string assetId,
                ISiteStatusQuery query,
                IClock clock,
                CancellationToken ct) =>
            {
                var view = await query.FindAsync(assetId, clock.UtcNow, ct).ConfigureAwait(false);
                if (view is null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(new SiteStatusResponse(
                    AssetId: view.AssetId,
                    Telemetry: view.Telemetry is null ? null : SiteTelemetryView.From(view.Telemetry),
                    Quality: view.Quality is null ? null : DataQualityView.From(view.Quality),
                    ObservedAt: view.ObservedAt));
            })
            .WithName("SiteStatus")
            .WithSummary("Current site power telemetry.");

        return routes;
    }
}
