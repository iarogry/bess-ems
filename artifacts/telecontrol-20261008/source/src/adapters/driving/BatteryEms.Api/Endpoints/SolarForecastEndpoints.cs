using BatteryEms.Api.Contracts;
using BatteryEms.Application.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class SolarForecastEndpoints
{
    public static IEndpointRouteBuilder MapSolarForecasts(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/site/{assetId}/solar-forecast", async (
                string assetId,
                ISolarForecastQuery query,
                CancellationToken ct) =>
            {
                var forecast = await query.FindAsync(assetId, ct).ConfigureAwait(false);
                if (forecast is null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(new SolarForecastResponse(
                    AssetId: forecast.AssetId,
                    Source: forecast.Source,
                    Model: forecast.Model,
                    GeneratedAt: forecast.GeneratedAt,
                    HorizonStart: forecast.HorizonStart,
                    HorizonEnd: forecast.HorizonEnd,
                    TimeStepSeconds: forecast.TimeStep.TotalSeconds,
                    InstalledDcKw: forecast.InstalledDcKw,
                    InstalledAcKw: forecast.InstalledAcKw,
                    Points: forecast.Points.Select(SolarForecastPointView.From).ToArray()));
            })
            .WithName("SolarForecast")
            .WithSummary("Latest PV generation forecast for a configured site asset.");

        return routes;
    }
}
