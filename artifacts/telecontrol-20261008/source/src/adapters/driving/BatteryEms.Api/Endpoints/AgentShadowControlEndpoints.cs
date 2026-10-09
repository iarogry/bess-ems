using System.Globalization;
using BatteryEms.Api.Auth;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

/// <summary>
/// Opt-in bounded control surface. It starts only internal shadow computation;
/// no command, activation or vendor payload is accepted by this route.
/// </summary>
public static class AgentShadowControlEndpoints
{
    public static IEndpointRouteBuilder MapAgentShadowControl(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(
                "/agent/sites/{siteId}/shadow-runs/{deliveryDate}",
                StartShadowRunAsync)
            .RequireAuthorization(AuthConstants.OperatorPolicy)
            .WithName("StartAgentShadowRun")
            .WithSummary("Starts an idempotent read-only day-ahead shadow comparison run.");

        return routes;
    }

    private static async Task<IResult> StartShadowRunAsync(
        string siteId,
        string deliveryDate,
        IShadowRunTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return Results.BadRequest(new { error = "siteId is required." });
        }

        if (!DateOnly.TryParseExact(
                deliveryDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            return Results.BadRequest(new { error = "deliveryDate must use yyyy-MM-dd." });
        }

        var run = await trigger.StartAsync(
            siteId,
            parsedDate,
            OrchestrationTriggerType.Manual,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(AgentShadowRunResponse.From(run, parsedDate));
    }
}
