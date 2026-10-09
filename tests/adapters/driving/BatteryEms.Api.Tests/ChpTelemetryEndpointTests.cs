using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Application.Realtime;
using BatteryEms.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class ChpTelemetryEndpointTests
{
    [Fact]
    public async Task Missing_chp_is_unavailable_instead_of_zero()
    {
        using var factory = new BatteryEmsApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/chp/missing/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(0, "substituted")]
    [InlineData(-5, "stale")]
    public async Task Chp_read_endpoint_keeps_parameters_quality_and_source_identity(int minutes, string quality)
    {
        using var factory = new BatteryEmsApiFactory();
        var now = DateTimeOffset.UtcNow;
        factory.Services.GetRequiredService<IChpTelemetryStore>().Update(new ChpTelemetry(
            "telecontrol-5552", 5552, now.AddMinutes(minutes), now, "2026-10-08T14:32:56", 0,
            DataQuality.Substituted("telecontrol-source-timezone-provisional"),
            [new ChpParameter("Betriebsstunden", "h", "3133", 3133, true)],
            [new ChpMessage("DigitalErrors", 74, 0)]));
        using var client = factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/chp/telecontrol-5552/status");
        Assert.Equal(5552, body.GetProperty("device_id").GetInt32());
        Assert.Equal(0, body.GetProperty("power_kw").GetDouble());
        Assert.Equal(quality, body.GetProperty("quality").GetProperty("flag").GetString());
        Assert.Equal(3133, body.GetProperty("parameters")[0].GetProperty("numeric_value").GetDouble());
        Assert.False(body.TryGetProperty("password", out _));
    }
}
