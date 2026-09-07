using System.Globalization;
using BatteryEms.Adapters.OpenMeteo;
using BatteryEms.Application.Forecasting;
using BatteryEms.Application.Site;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BatteryEms.Adapters.OpenMeteo.Tests;

public sealed class OpenMeteoSolarForecastHostedServiceTests
{
    [Fact]
    public async Task Hosted_service_refreshes_enabled_site_pv_profiles_into_store()
    {
        var provider = new FakeProvider();
        var store = new InMemorySolarForecastStore();
        var profiles = new InMemorySitePvProfileStore();
        await profiles.UpsertAsync(new SitePvProfile(
            SiteId: "site-1",
            PvSystemId: "pv-roof",
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
            ForecastEngine: "pvlib_sidecar"), CancellationToken.None);

        var service = new OpenMeteoSolarForecastHostedService(
            provider,
            store,
            profiles,
            Options.Create(new OpenMeteoSolarForecastOptions
            {
                ForecastRefreshSeconds = 3600,
                EngineBackend = "pvlib_sidecar",
                PythonExecutable = "python",
                SidecarScriptPath = "tools/solar-forecast-engine/pvlib_openmeteo_engine.py",
            }),
            TimeProvider.System,
            NullLogger<OpenMeteoSolarForecastHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        var forecast = store.GetLatest("site-1-pv-roof");
        Assert.NotNull(forecast);
        Assert.Equal("site-1-pv-roof", forecast!.AssetId);
        Assert.Equal(1, provider.ProfileLoadCount);
    }

    private sealed class FakeProvider : ISolarForecastProvider
    {
        public int ProfileLoadCount { get; private set; }

        public Task<SolarForecast> LoadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Default single-asset path should not be used in this test.");

        public Task<SolarForecast> LoadAsync(OpenMeteoSolarForecastOptions options, CancellationToken cancellationToken)
        {
            ProfileLoadCount++;
            return Task.FromResult(new SolarForecast(
                assetId: options.AssetId!,
                source: "open-meteo",
                model: "fake-provider",
                generatedAt: DateTimeOffset.Parse("2026-06-05T08:00:00Z", CultureInfo.InvariantCulture),
                horizonStart: DateTimeOffset.Parse("2026-06-05T09:00:00Z", CultureInfo.InvariantCulture),
                horizonEnd: DateTimeOffset.Parse("2026-06-05T09:15:00Z", CultureInfo.InvariantCulture),
                timeStep: TimeSpan.FromMinutes(15),
                installedDcKw: options.InstalledDcKw,
                installedAcKw: options.InverterAcKw,
                points:
                [
                    new SolarForecastPoint(
                        DateTimeOffset.Parse("2026-06-05T09:00:00Z", CultureInfo.InvariantCulture),
                        100,
                        500,
                        25,
                        2,
                        10),
                ]));
        }
    }
}
