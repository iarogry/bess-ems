using System.Security.Claims;
using System.Text.Json;
using BatteryEms.Api.Auth;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;
using BatteryEms.Application.Time;

namespace BatteryEms.Host.Planning;

public static class DeyeDayAuthorizationEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static void MapDeyeDayAuthorization(this WebApplication app, EmsPlanningOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || !options.DayAuthorizationsEnabled) { return; }
        app.MapPost("/agent/activation-proposals/{proposalId:guid}/day-authorization", AuthorizeAsync)
            .RequireAuthorization(AuthConstants.OperatorPolicy).WithName("AuthorizeDeyeDeliveryDay");
        app.MapGet("/agent/sites/{siteId}/day-authorizations/{deliveryDate}", async (string siteId,
            DateOnly deliveryDate, IDeyeDayAuthorizationStore store, CancellationToken token) =>
        {
            var authorization = await store.FindAsync(siteId, deliveryDate, token).ConfigureAwait(false);
            return authorization is null ? Results.NotFound() : Results.Ok(authorization);
        }).RequireAuthorization(AuthConstants.AgentReadPolicy).WithName("GetDeyeDayAuthorization");
        app.MapPost("/agent/day-authorizations/{authorizationId:guid}/revoke", async (Guid authorizationId,
            DeyeDayRevokeBody body, ClaimsPrincipal user, IDeyeDayAuthorizationStore store, IClock clock, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) { return Results.BadRequest(); }
            var revoked = await store.RevokeAsync(authorizationId, Actor(user), body.Reason, clock.UtcNow, token).ConfigureAwait(false);
            return revoked ? Results.Ok() : Results.Conflict(new { error = "deye-day-authorization-not-revoked" });
        }).RequireAuthorization(AuthConstants.OperatorPolicy).WithName("RevokeDeyeDayAuthorization");
    }

    private static async Task<IResult> AuthorizeAsync(Guid proposalId, DeyeDayApprovalBody body,
        ClaimsPrincipal user, EmsPlanningOptions options, IEquipmentDayPlanStore plans,
        IActivationProposalStore proposals, IDeyeDayAuthorizationStore store, IClock clock, CancellationToken token)
    {
        if (body.AuthorizationId == Guid.Empty || body.SafetyRevision < 1 || string.IsNullOrWhiteSpace(body.Reason))
        { return Results.BadRequest(); }
        var proposal = await proposals.FindAsync(proposalId, token).ConfigureAwait(false);
        if (proposal is null) { return Results.NotFound(); }
        var target = options.Targets.SingleOrDefault(item => item.SiteId == proposal.SiteId && item.IntegrationId == "deye_cloud");
        if (target is null) { return Results.Conflict(new { error = "deye-day-target-missing" }); }
        var plan = await plans.FindAsync(target.AssetId, proposal.DeliveryDate, token).ConfigureAwait(false);
        if (plan is null || plan.Actions.Count != 4 || plan.Actions.Any(action =>
            JsonSerializer.Deserialize<ShadowPlanSnapshot>(action.PayloadJson, JsonOptions) is not { } snapshot
            || ActivationPayloadIntegrity.ComputeHash(snapshot) != proposal.PayloadHash))
        { return Results.Conflict(new { error = "deye-day-saved-plan-approval-mismatch" }); }
        var result = await store.AuthorizeAsync(new(body.AuthorizationId, proposalId, options.WriterOwnerId!,
            body.SafetyRevision, Actor(user), body.Reason, clock.UtcNow), token).ConfigureAwait(false);
        return result.Accepted ? Results.Ok(result.Authorization) : Results.Conflict(new { error = result.BlockingCode });
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? AuthConstants.AnonymousOperator;
}

public sealed record DeyeDayApprovalBody(Guid AuthorizationId, long SafetyRevision, string Reason);
public sealed record DeyeDayRevokeBody(string Reason);
