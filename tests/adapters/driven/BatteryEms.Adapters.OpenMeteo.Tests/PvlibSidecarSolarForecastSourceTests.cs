using System.Globalization;
using BatteryEms.Adapters.OpenMeteo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BatteryEms.Adapters.OpenMeteo.Tests;

public sealed class PvlibSidecarSolarForecastSourceTests
{
    [Fact]
    public async Task LoadAsync_invokes_sidecar_and_maps_payload()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                """
                {
                  "asset_id": "single-bess-1",
                  "source": "open-meteo",
                  "model": "pvlib-pvwatts-open-meteo",
                  "generated_at": "2026-06-05T09:00:00Z",
                  "horizon_start": "2026-06-05T10:00:00Z",
                  "horizon_end": "2026-06-05T10:30:00Z",
                  "time_step_seconds": 900,
                  "installed_dc_kw": 900,
                  "installed_ac_kw": 800,
                  "points": [
                    {
                      "timestamp": "2026-06-05T10:00:00Z",
                      "power_kw": 333.723,
                      "irradiance_w_per_square_meter": 463.3,
                      "ambient_temperature_celsius": 24.5,
                      "wind_speed_meters_per_second": 4.3,
                      "cloud_cover_percent": 100
                    },
                    {
                      "timestamp": "2026-06-05T10:15:00Z",
                      "power_kw": 295.884,
                      "irradiance_w_per_square_meter": 410.275,
                      "ambient_temperature_celsius": 24.55,
                      "wind_speed_meters_per_second": 4.4,
                      "cloud_cover_percent": 100
                    }
                  ]
                }
                """,
                string.Empty)));
        var source = CreateSource(runner);

        var forecast = await source.LoadAsync(CancellationToken.None);

        Assert.Equal("single-bess-1", forecast.AssetId);
        Assert.Equal("pvlib-pvwatts-open-meteo", forecast.Model);
        Assert.Equal(2, forecast.Points.Count);
        await runner.Received(1).RunAsync(
            Arg.Is<ProcessRunRequest>(request =>
                request.FileName == "python"
                && request.Arguments.Contains("tools/solar-forecast-engine/pvlib_openmeteo_engine.py")
                && request.Arguments.Contains("--stdin-json")
                && request.StandardInput != null
                && request.StandardInput.Contains("\"asset_id\":\"single-bess-1\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ToSolarForecast_maps_wire_payload_to_domain_model()
    {
        var response = new PvlibSidecarResponse
        {
            AssetId = "site-1",
            Source = "open-meteo",
            Model = "pvlib-pvwatts-open-meteo",
            GeneratedAt = "2026-06-05T09:00:00Z",
            HorizonStart = "2026-06-05T10:00:00Z",
            HorizonEnd = "2026-06-05T10:30:00Z",
            TimeStepSeconds = 900,
            InstalledDcKw = 900,
            InstalledAcKw = 800,
            Points =
            [
                new PvlibSidecarPoint
                {
                    Timestamp = "2026-06-05T10:00:00Z",
                    PowerKw = 1,
                    IrradianceWPerSquareMeter = 100,
                    AmbientTemperatureCelsius = 20,
                    WindSpeedMetersPerSecond = 3,
                    CloudCoverPercent = 5,
                },
                new PvlibSidecarPoint
                {
                    Timestamp = "2026-06-05T10:15:00Z",
                    PowerKw = 2,
                    IrradianceWPerSquareMeter = 200,
                    AmbientTemperatureCelsius = 21,
                    WindSpeedMetersPerSecond = 4,
                    CloudCoverPercent = 10,
                },
            ],
        };

        var forecast = PvlibSidecarSolarForecastSource.ToSolarForecast(response);

        Assert.Equal(DateTimeOffset.Parse("2026-06-05T09:00:00Z", CultureInfo.InvariantCulture), forecast.GeneratedAt);
        Assert.Equal(900, forecast.InstalledDcKw);
        Assert.Equal(800, forecast.InstalledAcKw);
        Assert.Equal(2, forecast.Points.Count);
    }

    private static PvlibSidecarSolarForecastSource CreateSource(IProcessRunner runner)
    {
        var options = Options.Create(new OpenMeteoSolarForecastOptions
        {
            AssetId = "single-bess-1",
            Latitude = 49.84,
            Longitude = 24.03,
            InstalledDcKw = 900,
            InverterAcKw = 800,
            EngineBackend = "pvlib_sidecar",
            PythonExecutable = "python",
            SidecarScriptPath = "tools/solar-forecast-engine/pvlib_openmeteo_engine.py",
        });
        return new PvlibSidecarSolarForecastSource(
            options,
            runner,
            NullLogger<PvlibSidecarSolarForecastSource>.Instance);
    }
}
