using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Site;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class SitePvProfileEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private readonly BatteryEmsApiFactory _factory;

    public SitePvProfileEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("invalid-test-token", HttpStatusCode.Unauthorized)]
    [InlineData(BatteryEmsApiFactory.ViewerToken, HttpStatusCode.Forbidden)]
    public async Task Profile_write_rejects_missing_invalid_or_insufficient_credentials_without_mutation(
        string? token, HttpStatusCode expected)
    {
        using var client = _factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await client.PutAsJsonAsync(
            "/site/site-pv-denied/pv-profiles/pv-1", ValidProfile(), TestJson.Options);

        Assert.Equal(expected, response.StatusCode);
        var store = _factory.Services.GetRequiredService<ISitePvProfileStore>();
        Assert.Null(await store.FindAsync("site-pv-denied", "pv-1", CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_write_is_rejected_before_payload_validation()
    {
        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            "/site/site-pv-invalid-anonymous/pv-profiles/pv-1", new { }, TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Operator_can_create_and_update_profile_visible_to_read_only_clients()
    {
        using var client = OperatorClient();
        var profile = ValidProfile();
        var created = await client.PutAsJsonAsync(
            "/site/site-pv-accepted/pv-profiles/pv-1", profile, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var updated = await client.PutAsJsonAsync(
            "/site/site-pv-accepted/pv-profiles/pv-1", profile with { Name = "Updated test PV" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var reader = _factory.CreateClient();
        var listed = await reader.GetFromJsonAsync<SitePvProfilesResponse>(
            "/site/site-pv-accepted/pv-profiles", TestJson.Options);
        Assert.NotNull(listed);
        var stored = Assert.Single(listed.Profiles);
        Assert.Equal("pv-1", stored.PvSystemId);
        Assert.Equal("Updated test PV", stored.Name);
        Assert.False(stored.Enabled);
    }

    [Fact]
    public async Task Operator_invalid_payload_returns_bad_request_without_mutation()
    {
        using var client = OperatorClient();
        var response = await client.PutAsJsonAsync(
            "/site/site-pv-invalid-operator/pv-profiles/pv-1", ValidProfile() with { Name = "" }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var store = _factory.Services.GetRequiredService<ISitePvProfileStore>();
        Assert.Null(await store.FindAsync("site-pv-invalid-operator", "pv-1", CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_profile_read_remains_available()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/site/site-pv-empty/pv-profiles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = await response.Content.ReadFromJsonAsync<SitePvProfilesResponse>(TestJson.Options);
        Assert.NotNull(listed);
        Assert.Empty(listed.Profiles);
    }

    private HttpClient OperatorClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BatteryEmsApiFactory.OperatorToken);
        return client;
    }

    private static UpsertSitePvProfileRequest ValidProfile() => new(
        Name: "Test PV", ForecastAssetId: "test-forecast", Enabled: false,
        Latitude: 50, Longitude: 30, TiltDegrees: 30, AzimuthDegrees: 0,
        InstalledDcKw: 100, InverterAcKw: 90, TemperatureCoefficientPerDegree: -0.004,
        SystemLossFraction: 0.1, ForecastHorizonHours: 24, ForecastResolutionMinutes: 60,
        ForecastProvider: "test-only", ForecastEngine: "test-only", Notes: "Synthetic contract test");
}
