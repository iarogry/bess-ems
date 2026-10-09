using System.Globalization;
using System.Security.Claims;
using BatteryEms.Api.Auth;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class AgentActivationProposalEndpoints
{
    public static IEndpointRouteBuilder MapAgentActivationProposals(
        this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost(
                "/agent/sites/{siteId}/activation-proposals/{deliveryDate}",
                ProposeAsync)
            .RequireAuthorization(AuthConstants.OperatorPolicy)
            .WithName("CreateAgentActivationProposal")
            .WithSummary("Creates an expiring proposal from an equivalent shadow comparison.");
        routes.MapPost(
                "/agent/activation-proposals/{proposalId:guid}/approve",
                ApproveAsync)
            .RequireAuthorization(AuthConstants.OperatorPolicy)
            .WithName("ApproveAgentActivationProposal")
            .WithSummary("Four-eyes approval creates a held, non-dispatchable outbox item.");
        routes.MapGet(
                "/agent/activation-proposals/{proposalId:guid}",
                GetAsync)
            .RequireAuthorization(AuthConstants.AgentReadPolicy)
            .WithName("GetAgentActivationProposal")
            .WithSummary("Returns sanitized proposal and held-outbox status.");
        return routes;
    }

    private static async Task<IResult> ProposeAsync(
        string siteId,
        string deliveryDate,
        ActivationProposalRequest? request,
        ClaimsPrincipal user,
        IActivationProposalUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteId)
            || request is null
            || string.IsNullOrWhiteSpace(request.Reason)
            || !DateOnly.TryParseExact(
                deliveryDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            return Results.BadRequest(new { error = "missing-or-invalid-field" });
        }

        var actor = Actor(user);
        var result = await useCase.ProposeAsync(
            siteId,
            parsedDate,
            actor,
            request.Reason,
            cancellationToken).ConfigureAwait(false);
        if (result.Proposal is null)
        {
            return Results.Conflict(new { error = result.ErrorCode });
        }

        var response = ActivationProposalResponse.From(result.Proposal);
        return result.Created
            ? Results.Created($"/agent/activation-proposals/{result.Proposal.ProposalId:D}", response)
            : Results.Ok(response);
    }

    private static async Task<IResult> ApproveAsync(
        Guid proposalId,
        ActivationProposalRequest? request,
        ClaimsPrincipal user,
        IActivationProposalUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.BadRequest(new { error = "missing-or-invalid-field" });
        }

        var result = await useCase.ApproveAsync(
            proposalId,
            Actor(user),
            request.Reason,
            cancellationToken).ConfigureAwait(false);
        if (result.Proposal is null)
        {
            return Results.NotFound(new { error = result.ErrorCode });
        }

        return result.Approved
            ? Results.Ok(ActivationProposalResponse.From(result.Proposal, result.OutboxItem))
            : Results.Conflict(new { error = result.ErrorCode });
    }

    private static async Task<IResult> GetAsync(
        Guid proposalId,
        IActivationProposalUseCase useCase,
        CancellationToken cancellationToken)
    {
        var proposal = await useCase.FindAsync(proposalId, cancellationToken).ConfigureAwait(false);
        return proposal is null
            ? Results.NotFound(new { error = "activation-proposal-not-found" })
            : Results.Ok(ActivationProposalResponse.From(proposal));
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? AuthConstants.AnonymousOperator;
}
