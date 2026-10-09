using System.Security.Claims;
using BatteryEms.Api.Auth;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class AgentActivationCutoverEndpoints
{
    public static IEndpointRouteBuilder MapAgentActivationCutover(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost(
                "/agent/activation-proposals/{proposalId:guid}/release",
                ReleaseAsync)
            .RequireAuthorization(AuthConstants.OperatorPolicy)
            .WithName("ReleaseAgentActivationOutbox")
            .WithSummary("Atomically releases one held item against safety revision and writer fence.");
        routes.MapPost(
                "/agent/sites/{siteId}/writer-safety/rollback",
                RollbackAsync)
            .RequireAuthorization(AuthConstants.OperatorPolicy)
            .WithName("RollbackAgentActivationWriter")
            .WithSummary("Engages the kill switch, revokes product authority and cancels ready items.");
        return routes;
    }

    private static async Task<IResult> ReleaseAsync(
        Guid proposalId,
        ActivationOutboxReleaseRequestBody? request,
        ClaimsPrincipal user,
        IActivationCutoverUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.PilotSessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.WriterOwnerId)
            || string.IsNullOrWhiteSpace(request.Reason)
            || request.ExpectedSafetyRevision < 1
            || request.ExpectedFencingToken < 1)
        {
            return Results.BadRequest(new { error = "missing-or-invalid-field" });
        }

        var result = await useCase.ReleaseAsync(
            proposalId,
            request.PilotSessionId,
            request.WriterOwnerId,
            request.ExpectedSafetyRevision,
            request.ExpectedFencingToken,
            Actor(user),
            request.Reason,
            cancellationToken).ConfigureAwait(false);
        if (!result.Released || result.Proposal is null || result.OutboxItem is null)
        {
            return string.Equals(
                result.ErrorCode,
                "activation-proposal-not-found",
                StringComparison.Ordinal)
                ? Results.NotFound(new { error = result.ErrorCode })
                : Results.Conflict(new { error = result.ErrorCode });
        }

        return Results.Ok(ActivationOutboxReleaseResponse.From(
            result.Proposal,
            result.OutboxItem,
            result.Released));
    }

    private static async Task<IResult> RollbackAsync(
        string siteId,
        ActivationRollbackRequestBody? request,
        ClaimsPrincipal user,
        IActivationCutoverUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteId)
            || request is null
            || request.OperationId == Guid.Empty
            || request.ExpectedSafetyRevision < 1
            || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.BadRequest(new { error = "missing-or-invalid-field" });
        }

        var result = await useCase.RollbackAsync(
            request.OperationId,
            siteId,
            request.ExpectedSafetyRevision,
            Actor(user),
            request.Reason,
            cancellationToken).ConfigureAwait(false);
        return result.RolledBack && result.SafetyState is not null
            ? Results.Ok(ActivationRollbackResponse.From(
                result.SafetyState,
                result.CancelledReadyItems,
                result.RolledBack))
            : Results.Conflict(new { error = result.ErrorCode });
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? AuthConstants.AnonymousOperator;
}
