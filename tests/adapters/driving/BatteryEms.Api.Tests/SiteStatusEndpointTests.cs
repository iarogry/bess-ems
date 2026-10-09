using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Assets;
using BatteryEms.Application.Api;
using BatteryEms.Application.Forecasting;
using BatteryEms.Application.Realtime;
using BatteryEms.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class SiteStatusEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private readonly BatteryEmsApiFactory _factory;

    public SiteStatusEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Status_returns_404_when_asset_is_not_registered()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/site/ghost-asset/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Status_returns_current_site_telemetry_for_known_asset()
    {
        using var scope = _factory.Services.CreateScope();
        var assets = scope.ServiceProvider.GetRequiredService<IBatteryAssetRegistry>() as InMemoryBatteryAssetRegistry;
        Assert.NotNull(assets);
        var asset = SampleAsset();
        assets!.Register(asset);

        var siteTelemetry = scope.ServiceProvider.GetRequiredService<ISiteTelemetryStore>();
        var timestamp = DateTimeOffset.UtcNow;
        siteTelemetry.Update(
            new SiteTelemetry(
                Timestamp: timestamp,
                AssetId: asset.AssetId,
                PvPowerKw: 77.79,
                LoadPowerKw: -18.452,
                GridPowerKw: null,
                IrradianceWPerSquareMeter: 512,
                DataQuality: DataQuality.Valid),
            timestamp);

        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/site/{asset.AssetId}/status");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SiteStatusDto>(TestJson.Options);

        Assert.NotNull(body);
        Assert.Equal(asset.AssetId, body!.AssetId);
        Assert.NotNull(body.Telemetry);
        Assert.Equal(77.79, body.Telemetry!.PvPowerKw);
        Assert.Equal(-18.452, body.Telemetry.LoadPowerKw);
        Assert.Null(body.Telemetry.GridPowerKw);
        Assert.Equal("Valid", body.Quality!.Flag);
    }

    [Fact]
    public async Task Solar_forecast_returns_latest_curve_for_known_asset()
    {
        using var scope = _factory.Services.CreateScope();
        var assets = scope.ServiceProvider.GetRequiredService<IBatteryAssetRegistry>() as InMemoryBatteryAssetRegistry;
        Assert.NotNull(assets);
        var asset = SampleAsset("forecast-status");
        assets!.Register(asset);

        var forecasts = scope.ServiceProvider.GetRequiredService<ISolarForecastStore>();
        forecasts.Update(new SolarForecast(
            assetId: asset.AssetId,
            source: "open-meteo",
            model: "open-meteo-gti-pvwatts-lite",
            generatedAt: DateTimeOffset.Parse("2026-06-05T08:55:00Z", CultureInfo.InvariantCulture),
            horizonStart: DateTimeOffset.Parse("2026-06-05T09:00:00Z", CultureInfo.InvariantCulture),
            horizonEnd: DateTimeOffset.Parse("2026-06-05T09:30:00Z", CultureInfo.InvariantCulture),
            timeStep: TimeSpan.FromMinutes(15),
            installedDcKw: 900,
            installedAcKw: 800,
            points:
            [
                new SolarForecastPoint(DateTimeOffset.Parse("2026-06-05T09:00:00Z", CultureInfo.InvariantCulture), 215.3, 421, 23.5, 2.4, 18),
                new SolarForecastPoint(DateTimeOffset.Parse("2026-06-05T09:15:00Z", CultureInfo.InvariantCulture), 244.6, 463, 24.1, 2.8, 16),
            ]));
        var query = scope.ServiceProvider.GetRequiredService<ISolarForecastQuery>();
        Assert.NotNull(await query.FindAsync(asset.AssetId, CancellationToken.None));

        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/site/{asset.AssetId}/solar-forecast");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SolarForecastDto>(TestJson.Options);

        Assert.NotNull(body);
        Assert.Equal(asset.AssetId, body!.AssetId);
        Assert.Equal("open-meteo", body.Source);
        Assert.Equal(2, body.Points.Count);
        Assert.Equal(215.3, body.Points[0].PowerKw);
        Assert.Equal(15 * 60, body.TimeStepSeconds);
    }

    [Fact]
    public async Task Site_pv_profile_can_be_upserted_and_listed()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BatteryEmsApiFactory.OperatorToken);

        var put = await client.PutAsJsonAsync(
            "/site/site-1/pv-profiles/pv-roof",
            new UpsertSitePvProfileRequest(
                Name: "Roof PV",
                ForecastAssetId: "site-1-pv-roof",
                Enabled: true,
                Latitude: 50.45,
                Longitude: 30.52,
                TiltDegrees: 25,
                AzimuthDegrees: 0,
                InstalledDcKw: 500,
                InverterAcKw: 450,
                TemperatureCoefficientPerDegree: -0.004,
                SystemLossFraction: 0.14,
                ForecastHorizonHours: 48,
                ForecastResolutionMinutes: 15,
                ForecastProvider: "open_meteo",
                ForecastEngine: "pvlib_sidecar"),
            TestJson.Options);
        put.EnsureSuccessStatusCode();

        client.DefaultRequestHeaders.Authorization = null;
        var list = await client.GetAsync("/site/site-1/pv-profiles");
        list.EnsureSuccessStatusCode();
        var body = await list.Content.ReadFromJsonAsync<SitePvProfilesDto>(TestJson.Options);

        Assert.NotNull(body);
        Assert.Equal("site-1", body!.SiteId);
        Assert.Single(body.Profiles);
        Assert.Equal("pv-roof", body.Profiles[0].PvSystemId);
        Assert.Equal("site-1-pv-roof", body.Profiles[0].ForecastAssetId);
    }

    private static BatteryAsset SampleAsset(string id = "site-status") => new(
        assetId: id,
        capacityKwh: 100,
        maxChargePowerKw: 50,
        maxDischargePowerKw: 50,
        minSocPercent: 10,
        maxSocPercent: 90,
        chargeEfficiency: 0.95,
        dischargeEfficiency: 0.95,
        maxRampKwPerSecond: 25,
        minOperatingTemperatureCelsius: -20,
        maxOperatingTemperatureCelsius: 55);

    private sealed record SiteStatusDto(
        string AssetId,
        SiteTelemetryDto? Telemetry,
        DataQualityDto? Quality,
        DateTimeOffset? ObservedAt);

    private sealed record SiteTelemetryDto(
        DateTimeOffset Timestamp,
        double? PvPowerKw,
        double? LoadPowerKw,
        double? GridPowerKw,
        double? IrradianceWPerSquareMeter);

    private sealed record SolarForecastDto(
        string AssetId,
        string Source,
        string Model,
        DateTimeOffset GeneratedAt,
        DateTimeOffset HorizonStart,
        DateTimeOffset HorizonEnd,
        double TimeStepSeconds,
        double InstalledDcKw,
        double InstalledAcKw,
        IReadOnlyList<SolarForecastPointDto> Points);

    private sealed record SolarForecastPointDto(
        DateTimeOffset Timestamp,
        double PowerKw,
        double IrradianceWPerSquareMeter,
        double AmbientTemperatureCelsius,
        double WindSpeedMetersPerSecond,
        int CloudCoverPercent);

    private sealed record SitePvProfilesDto(
        string SiteId,
        IReadOnlyList<SitePvProfileDto> Profiles);

    private sealed record SitePvProfileDto(
        string SiteId,
        string PvSystemId,
        string Name,
        string ForecastAssetId,
        bool Enabled,
        double Latitude,
        double Longitude,
        double TiltDegrees,
        double AzimuthDegrees,
        double InstalledDcKw,
        double InverterAcKw,
        double TemperatureCoefficientPerDegree,
        double SystemLossFraction,
        int ForecastHorizonHours,
        int ForecastResolutionMinutes,
        string ForecastProvider,
        string ForecastEngine,
        string? Notes);

    private sealed record DataQualityDto(string Flag, string Reason);
}
