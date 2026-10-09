using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Api.Contracts;
using BatteryEms.Application.Orchestration;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class AgentActivationPilotEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private readonly BatteryEmsApiFactory _factory;

    public AgentActivationPilotEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_and_viewer_cannot_arm_pilot()
    {
        var path = $"/agent/activation-proposals/{Guid.NewGuid():D}/pilot-sessions";
        var body = ArmBody();
        using var anonymous = _factory.CreateClient();
        using var viewer = Client(BatteryEmsApiFactory.ViewerToken);

        var anonymousResponse = await anonymous.PostAsJsonAsync(path, body, TestJson.Options);
        var viewerResponse = await viewer.PostAsJsonAsync(path, body, TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);
    }

    [Fact]
    public async Task In_memory_host_fails_closed_for_operator()
    {
        using var client = Client(BatteryEmsApiFactory.OperatorToken);

        var response = await client.PostAsJsonAsync(
            $"/agent/activation-proposals/{Guid.NewGuid():D}/pilot-sessions",
            ArmBody(),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("activation-durable-persistence-required", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_session_returns_not_found_to_viewer()
    {
        using var client = Client(BatteryEmsApiFactory.ViewerToken);

        var response = await client.GetAsync(
            $"/agent/activation-pilot-sessions/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_window_is_rejected_before_use_case()
    {
        using var client = Client(BatteryEmsApiFactory.OperatorToken);

        var response = await client.PostAsJsonAsync(
            $"/agent/activation-proposals/{Guid.NewGuid():D}/pilot-sessions",
            new
            {
                session_id = Guid.NewGuid(),
                window_id = "Z5",
                writer_owner_id = "agent-instance-a",
                expected_safety_revision = 2,
                expected_fencing_token = 7,
                reason = "invalid window",
            },
            TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Read_contract_omits_owner_fence_reasons_and_operator_identities()
    {
        var session = new ActivationPilotSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "site-1",
            "Z1",
            "sensitive-owner",
            2,
            12345,
            ActivationPilotSessionStatus.Armed,
            "sensitive-operator",
            "sensitive-reason",
            DateTimeOffset.Parse("2026-09-25T12:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-25T12:15:00Z", CultureInfo.InvariantCulture)).EnsureValid();

        var json = JsonSerializer.Serialize(
            ActivationPilotSessionResponse.From(session),
            TestJson.Options);

        Assert.DoesNotContain("sensitive-owner", json, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-operator", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-reason", json, StringComparison.Ordinal);
        Assert.Contains("Z1", json, StringComparison.Ordinal);
    }

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object ArmBody() => new
    {
        session_id = Guid.NewGuid(),
        window_id = "Z1",
        writer_owner_id = "agent-instance-a",
        expected_safety_revision = 2,
        expected_fencing_token = 7,
        reason = "bounded pilot",
    };
}
