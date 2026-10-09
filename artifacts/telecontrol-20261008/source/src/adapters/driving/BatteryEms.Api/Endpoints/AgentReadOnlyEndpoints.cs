using System.Globalization;
using BatteryEms.Api.Auth;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

/// <summary>
/// Authenticated, bounded read surface for the supervisory agent. No
/// activation, command, credential or vendor-transport type is accepted.
/// </summary>
public static class AgentReadOnlyEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(
        this IEndpointRouteBuilder routes,
        bool shadowControlEnabled,
        bool activationCutoverEnabled = false,
        bool activationPilotEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (activationCutoverEnabled && !activationPilotEnabled)
        {
            throw new InvalidOperationException(
                "Activation cutover endpoints require activation pilot control to be enabled.");
        }

        routes.MapAgentReadOnly();
        if (shadowControlEnabled)
        {
            routes.MapAgentShadowControl();
            routes.MapAgentActivationProposals();
        }

        if (activationCutoverEnabled)
        {
            if (!shadowControlEnabled)
            {
                throw new InvalidOperationException(
                    "Activation cutover endpoints require shadow control to be enabled.");
            }

            routes.MapAgentActivationCutover();
        }

        if (activationPilotEnabled)
        {
            if (!activationCutoverEnabled)
            {
                throw new InvalidOperationException(
                    "Activation pilot endpoints require cutover control to be enabled.");
            }

            routes.MapAgentActivationPilot();
        }

        return routes;
    }

    public static IEndpointRouteBuilder MapAgentEndpoints(
        this IEndpointRouteBuilder routes,
        string? shadowControlSetting,
        string? activationCutoverSetting = null,
        string? activationPilotSetting = null) =>
        routes.MapAgentEndpoints(
            string.Equals(
                shadowControlSetting,
                "true",
                StringComparison.OrdinalIgnoreCase),
            string.Equals(
                activationCutoverSetting,
                "true",
                StringComparison.OrdinalIgnoreCase),
            string.Equals(
                activationPilotSetting,
                "true",
                StringComparison.OrdinalIgnoreCase));

    public static IEndpointRouteBuilder MapAgentReadOnly(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(
                "/agent/sites/{siteId}/shadow-comparisons/latest",
                GetLatestShadowComparisonAsync)
            .RequireAuthorization(AuthConstants.AgentReadPolicy)
            .WithName("GetLatestAgentShadowComparison")
            .WithSummary("Returns the latest read-only legacy versus shadow plan comparison.");

        routes.MapGet(
                "/agent/sites/{siteId}/writer-safety",
                GetWriterSafetyAsync)
            .RequireAuthorization(AuthConstants.AgentReadPolicy)
            .WithName("GetAgentWriterSafety")
            .WithSummary("Returns sanitized, read-only writer safety and lease state.");

        routes.MapGet(
                "/agent/sites/{siteId}/pilot-readiness",
                GetPilotReadinessAsync)
            .RequireAuthorization(AuthConstants.AgentReadPolicy)
            .WithName("GetAgentPilotReadiness")
            .WithSummary("Evaluates the server-defined consecutive shadow-equivalence window.");

        return routes;
    }

    private static async Task<IResult> GetPilotReadinessAsync(
        string siteId,
        string? windowEnd,
        IPilotReadinessUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteId)
            || !DateOnly.TryParseExact(
                windowEnd,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedEnd))
        {
            return Results.BadRequest(new { error = "siteId and windowEnd=yyyy-MM-dd are required." });
        }

        var result = await useCase.EvaluateAsync(
            siteId,
            parsedEnd,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(AgentPilotReadinessResponse.From(result));
    }

    private static async Task<IResult> GetWriterSafetyAsync(
        string siteId,
        IActivationWriterSafetyStore store,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return Results.BadRequest(new { error = "siteId is required." });
        }

        var state = await store.FindStateAsync(siteId, cancellationToken).ConfigureAwait(false);
        var lease = await store.FindLeaseAsync(siteId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(AgentWriterSafetyResponse.From(siteId, state, lease, clock.UtcNow));
    }

    private static async Task<IResult> GetLatestShadowComparisonAsync(
        string siteId,
        string? deliveryDate,
        IShadowPlanComparisonStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return Results.BadRequest(new { error = "siteId is required." });
        }

        DateOnly? parsedDate = null;
        if (deliveryDate is not null)
        {
            if (!DateOnly.TryParseExact(
                    deliveryDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var value))
            {
                return Results.BadRequest(new { error = "deliveryDate must use yyyy-MM-dd." });
            }

            parsedDate = value;
        }

        var comparison = await store.FindLatestComparisonAsync(
            siteId,
            parsedDate,
            cancellationToken).ConfigureAwait(false);
        return comparison is null
            ? Results.NotFound(new { error = "shadow-comparison-not-found" })
            : Results.Ok(AgentShadowComparisonResponse.From(comparison));
    }
}
