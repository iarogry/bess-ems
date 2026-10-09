using BatteryEms.Adapters.OpenMeteo;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BatteryEms.Adapters.OpenMeteo.Tests;

public sealed class OpenMeteoSolarForecastSourceTests
{
    [Fact]
    public void EstimatePowerKw_returns_zero_for_zero_irradiance()
    {
        var source = CreateSource();

        var value = source.EstimatePowerKw(0, 20, 2);

        Assert.Equal(0, value);
    }

    [Fact]
    public void BuildHourlyPoints_converts_weather_payload_to_non_negative_power()
    {
        var source = CreateSource();
        var hourly = new OpenMeteoHourly
        {
            Time = ["2026-06-05T09:00", "2026-06-05T10:00"],
            GlobalTiltedIrradiance = [650, 810],
            Temperature2m = [24, 28],
            WindSpeed10m = [2.2, 2.8],
            CloudCover = [35, 20],
        };

        var points = source.BuildHourlyPoints(hourly);

        Assert.Equal(2, points.Count);
        Assert.Equal(650, points[0].IrradianceWPerSquareMeter);
        Assert.True(points[0].PowerKw > 0);
        Assert.True(points[1].PowerKw >= points[0].PowerKw);
    }

    [Fact]
    public void Resample_expands_hourly_points_to_fifteen_minutes()
    {
        var hourly = new[]
        {
            new BatteryEms.Application.Forecasting.SolarForecastPoint(
                DateTimeOffset.Parse("2026-06-05T09:00:00Z", CultureInfo.InvariantCulture),
                100,
                500,
                20,
                2,
                30),
            new BatteryEms.Application.Forecasting.SolarForecastPoint(
                DateTimeOffset.Parse("2026-06-05T10:00:00Z", CultureInfo.InvariantCulture),
                200,
                700,
                24,
                3,
                10),
        };

        var points = OpenMeteoSolarForecastSource.Resample(hourly, TimeSpan.FromMinutes(15));

        Assert.Equal(5, points.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-06-05T09:00:00Z", CultureInfo.InvariantCulture), points[0].Timestamp);
        Assert.Equal(DateTimeOffset.Parse("2026-06-05T10:00:00Z", CultureInfo.InvariantCulture), points[^1].Timestamp);
        Assert.Equal(150, points[2].PowerKw);
    }

    private static OpenMeteoSolarForecastSource CreateSource()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var options = Options.Create(new OpenMeteoSolarForecastOptions
        {
            AssetId = "pv-site-1",
            Latitude = 49.84,
            Longitude = 24.03,
            TiltDegrees = 25,
            AzimuthDegrees = 0,
            InstalledDcKw = 800,
            InverterAcKw = 700,
        });
        return new OpenMeteoSolarForecastSource(factory, options, NullLogger<OpenMeteoSolarForecastSource>.Instance);
    }
}
