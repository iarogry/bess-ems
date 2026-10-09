using BatteryEms.Api.Auth;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BatteryEms.Api.Endpoints;

public static class SitePvProfileEndpoints
{
    public static IEndpointRouteBuilder MapSitePvProfiles(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/site/{siteId}/pv-profiles", async (
                string siteId,
                ISitePvProfileStore store,
                CancellationToken ct) =>
            {
                var profiles = await store.ListBySiteAsync(siteId, ct).ConfigureAwait(false);
                return Results.Ok(new SitePvProfilesResponse(
                    siteId,
                    profiles.Select(SitePvProfileResponse.From).ToArray()));
            })
            .WithName("ListSitePvProfiles")
            .WithSummary("Lists configured PV profiles for a site.");

        routes.MapPut("/site/{siteId}/pv-profiles/{pvSystemId}", async (
                string siteId,
                string pvSystemId,
                UpsertSitePvProfileRequest request,
                ISitePvProfileStore store,
                CancellationToken ct) =>
            {
                SitePvProfile profile;
                try
                {
                    profile = new SitePvProfile(
                        SiteId: siteId,
                        PvSystemId: pvSystemId,
                        Name: request.Name,
                        ForecastAssetId: request.ForecastAssetId,
                        Enabled: request.Enabled,
                        Latitude: request.Latitude,
                        Longitude: request.Longitude,
                        TiltDegrees: request.TiltDegrees,
                        AzimuthDegrees: request.AzimuthDegrees,
                        InstalledDcKw: request.InstalledDcKw,
                        InverterAcKw: request.InverterAcKw,
                        TemperatureCoefficientPerDegree: request.TemperatureCoefficientPerDegree,
                        SystemLossFraction: request.SystemLossFraction,
                        ForecastHorizonHours: request.ForecastHorizonHours,
                        ForecastResolutionMinutes: request.ForecastResolutionMinutes,
                        ForecastProvider: request.ForecastProvider,
                        ForecastEngine: request.ForecastEngine,
                        Notes: request.Notes).EnsureValid();
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                await store.UpsertAsync(profile, ct).ConfigureAwait(false);

                return Results.Ok(SitePvProfileResponse.From(profile));
            })
            .WithName("UpsertSitePvProfile")
            .RequireAuthorization(AuthConstants.OperatorPolicy)
            .WithSummary("Creates or updates a PV profile for a site.");

        return routes;
    }
}
