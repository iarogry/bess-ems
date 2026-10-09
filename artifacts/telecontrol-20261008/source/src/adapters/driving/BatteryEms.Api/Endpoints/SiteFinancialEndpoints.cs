using System.Text.Json;
using BatteryEms.Api.Finance;
using BatteryEms.Application.Markets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class SiteFinancialEndpoints
{
    public static IEndpointRouteBuilder MapSiteFinancialPlans(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/site/{siteId}/financial-plan", async (
            string siteId, DateOnly? date, SiteFinancialPlanQuery query, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await query.FindAsync(siteId, date, DateTimeOffset.UtcNow, ct).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException
                or IOException or UnauthorizedAccessException or InvalidOperationException
                or HttpRequestException or PriceSeriesNotFoundException)
            {
                // Never return vendor exceptions, filesystem paths or credentials.
                return Results.Json(new { error = "financial_plan_unavailable" }, statusCode: 503);
            }
        }).WithName("SiteFinancialPlan").WithSummary("Current-day planned site economics and monthly review status.");
        return routes;
    }
}
