using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace BatteryEms.Api.Tests;

public sealed class AgentActivationCutoverEndpointTests : IClassFixture<BatteryEmsApiFactory>
{
    private readonly BatteryEmsApiFactory _factory;

    public AgentActivationCutoverEndpointTests(BatteryEmsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_and_viewer_cannot_release_outbox()
    {
        var path = $"/agent/activation-proposals/{Guid.NewGuid():D}/release";
        var body = ReleaseBody();
        using var anonymous = _factory.CreateClient();
        using var viewer = Client(BatteryEmsApiFactory.ViewerToken);

        var anonymousResponse = await anonymous.PostAsJsonAsync(path, body, TestJson.Options);
        var viewerResponse = await viewer.PostAsJsonAsync(path, body, TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);
    }

    [Fact]
    public async Task In_memory_host_rejects_release_even_for_operator()
    {
        using var client = Client(BatteryEmsApiFactory.OperatorToken);

        var response = await client.PostAsJsonAsync(
            $"/agent/activation-proposals/{Guid.NewGuid():D}/release",
            ReleaseBody(),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("activation-durable-persistence-required", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_memory_host_rejects_rollback_even_for_operator()
    {
        using var client = Client(BatteryEmsApiFactory.OperatorToken);
        var body = new
        {
            operation_id = Guid.NewGuid(),
            expected_safety_revision = 2,
            reason = "emergency rollback",
        };

        var response = await client.PostAsJsonAsync(
            "/agent/sites/site-1/writer-safety/rollback",
            body,
            TestJson.Options);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("activation-durable-persistence-required", raw, StringComparison.Ordinal);
    }

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object ReleaseBody() => new
    {
        pilot_session_id = Guid.NewGuid(),
        writer_owner_id = "agent-instance-a",
        expected_safety_revision = 2,
        expected_fencing_token = 7,
        reason = "controlled release",
    };
}
