using System.Security.Claims;
using BatteryEms.Api.Auth;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class AgentActivationPilotEndpoints
{
    public static IEndpointRouteBuilder MapAgentActivationPilot(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/agent/activation-proposals/{proposalId:guid}/pilot-sessions", ArmAsync)
            .RequireAuthorization(AuthConstants.OperatorPolicy)
            .WithName("ArmAgentActivationPilot")
            .WithSummary("Arms one fail-closed, maximum fifteen-minute pilot session.");
        routes.MapPost("/agent/activation-pilot-sessions/{sessionId:guid}/abort", AbortAsync)
            .RequireAuthorization(AuthConstants.OperatorPolicy)
            .WithName("AbortAgentActivationPilot")
            .WithSummary("Aborts an armed pilot session without dispatching anything.");
        routes.MapGet("/agent/activation-pilot-sessions/{sessionId:guid}", FindAsync)
            .RequireAuthorization(AuthConstants.AgentReadPolicy)
            .WithName("GetAgentActivationPilot")
            .WithSummary("Returns sanitized pilot-session state.");
        return routes;
    }

    private static async Task<IResult> ArmAsync(
        Guid proposalId,
        ActivationPilotArmRequestBody? request,
        ClaimsPrincipal user,
        IActivationPilotSessionUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (proposalId == Guid.Empty
            || request is null
            || request.SessionId == Guid.Empty
            || !ActivationPayloadIntegrity.IsWindowIdValid(request.WindowId)
            || string.IsNullOrWhiteSpace(request.WriterOwnerId)
            || request.ExpectedSafetyRevision < 1
            || request.ExpectedFencingToken < 1
            || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.BadRequest(new { error = "missing-or-invalid-field" });
        }

        var result = await useCase.ArmAsync(
            request.SessionId,
            proposalId,
            request.WindowId,
            request.WriterOwnerId,
            request.ExpectedSafetyRevision,
            request.ExpectedFencingToken,
            Actor(user),
            request.Reason,
            cancellationToken).ConfigureAwait(false);
        return result.Armed && result.Session is not null
            ? Results.Ok(ActivationPilotSessionResponse.From(result.Session))
            : Results.Conflict(new { error = result.ErrorCode });
    }

    private static async Task<IResult> AbortAsync(
        Guid sessionId,
        ActivationPilotAbortRequestBody? request,
        ClaimsPrincipal user,
        IActivationPilotSessionUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || request is null || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.BadRequest(new { error = "missing-or-invalid-field" });
        }

        var result = await useCase.AbortAsync(
            sessionId,
            Actor(user),
            request.Reason,
            cancellationToken).ConfigureAwait(false);
        return result.Aborted && result.Session is not null
            ? Results.Ok(ActivationPilotSessionResponse.From(result.Session))
            : Results.Conflict(new { error = result.ErrorCode });
    }

    private static async Task<IResult> FindAsync(
        Guid sessionId,
        IActivationPilotSessionUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing-or-invalid-field" });
        }

        var session = await useCase.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return session is null
            ? Results.NotFound(new { error = "activation-pilot-session-not-found" })
            : Results.Ok(ActivationPilotSessionResponse.From(session));
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? AuthConstants.AnonymousOperator;
}
