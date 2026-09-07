using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BatteryEms.Adapters.DeyeCloud;
using BatteryEms.Application.Realtime;
using BatteryEms.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BatteryEms.Adapters.DeyeCloud.Tests;

public sealed class DeyeCloudTelemetrySourceTests
{
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ILogger<DeyeCloudTelemetrySource> _logger = Substitute.For<ILogger<DeyeCloudTelemetrySource>>();

    [Fact]
    public async Task ReadAsync_CallsStationLatest_WhenStationIdIsProvided()
    {
        // Arrange
        var options = new DeyeAdapterOptions
        {
            BaseUrl = "https://api.deye.com",
            AppId = "test-app-id",
            AppSecret = "test-secret",
            Email = "test@example.com",
            Password = "password",
            StationId = "station-123",
            AssetId = "single-bess-1"
        };

        var handler = new MockHttpMessageHandler();
        
        // Mock Token Response
        handler.AddResponse("https://api.deye.com/account/token?appId=test-app-id", new
        {
            code = 1000000,
            msg = "success",
            accessToken = "fake-token",
            tokenType = "bearer",
            expiresIn = "5183999"
        });

        // Real Deye station/latest shape returns metrics at the root, not under data.dataList.
        handler.AddResponse("https://api.deye.com/station/latest", new
        {
            code = 1000000,
            msg = "success",
            success = true,
            generationPower = 77790,
            consumptionPower = -18452,
            wirePower = -92421,
            chargePower = -1710,
            dischargePower = 600,
            batteryPower = -1110,
            batterySOC = 99.5,
            gridPower = -92421,
            irradiateIntensity = 512,
            lastUpdateTime = 1780405319
        });

        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("DeyeCloud").Returns(httpClient);

        var siteTelemetry = new InMemorySiteTelemetryStore(TimeSpan.FromSeconds(10));
        var source = new DeyeCloudTelemetrySource(_httpClientFactory, Options.Create(options), _logger, siteTelemetry);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        BatteryTelemetry? telemetry = null;
        await foreach (var t in source.ReadAsync(cts.Token))
        {
            telemetry = t;
            break;
        }

        // Assert
        Assert.NotNull(telemetry);
        Assert.Equal("single-bess-1", telemetry.AssetId);
        Assert.Equal(99.5, telemetry.SocPercent);
        Assert.Equal(-1.11, telemetry.ActivePowerKw, precision: 2);
        Assert.True(telemetry.Available);
        Assert.Equal(DataQualityState.Valid, telemetry.DataQuality.Flag);

        var siteSnapshot = siteTelemetry.GetLatest("single-bess-1", DateTimeOffset.UtcNow);
        Assert.NotNull(siteSnapshot);
        Assert.Equal(77.79, siteSnapshot!.Telemetry.PvPowerKw);
        Assert.Equal(-18.452, siteSnapshot.Telemetry.LoadPowerKw);
        Assert.Equal(-92.421, siteSnapshot.Telemetry.GridPowerKw);
        Assert.Equal(512, siteSnapshot.Telemetry.IrradianceWPerSquareMeter);

        var stationRequest = handler.Requests.FirstOrDefault(r => r.RequestUri?.AbsolutePath.EndsWith("station/latest", StringComparison.Ordinal) == true);
        Assert.NotNull(stationRequest);
    }

    [Fact]
    public async Task ReadAsync_CallsDeviceLatest_WhenOnlyDeviceSnIsProvided()
    {
        // Arrange
        var options = new DeyeAdapterOptions
        {
            BaseUrl = "https://api.deye.com",
            AppId = "test-app-id",
            AppSecret = "test-secret",
            Email = "test@example.com",
            Password = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            DeviceSn = "SN123"
        };

        var handler = new MockHttpMessageHandler();
        
        // Mock Token Response
        handler.AddResponse("https://api.deye.com/account/token?appId=test-app-id", new
        {
            code = 1000000,
            msg = "success",
            data = new { accessToken = "fake-token", expiresIn = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }
        });

        // Mock Device Latest Response
        handler.AddResponse("https://api.deye.com/device/latest", new
        {
            code = 1000000,
            msg = "success",
            data = new[]
            {
                new
                {
                    deviceSn = "SN123",
                    dataList = new[]
                    {
                        new { key = "batteryPower", value = "3000", unit = "W" },
                        new { key = "batCapcity", value = "70", unit = "%" }
                    }
                }
            }
        });

        var httpClient = new HttpClient(handler);
        _httpClientFactory.CreateClient("DeyeCloud").Returns(httpClient);

        var source = new DeyeCloudTelemetrySource(_httpClientFactory, Options.Create(options), _logger);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        BatteryTelemetry? telemetry = null;
        await foreach (var t in source.ReadAsync(cts.Token))
        {
            telemetry = t;
            break;
        }

        // Assert
        Assert.NotNull(telemetry);
        Assert.Equal("SN123", telemetry.AssetId);
        Assert.Equal(70.0, telemetry.SocPercent);
        Assert.Equal(3.0, telemetry.ActivePowerKw);
        
        var deviceRequest = handler.Requests.FirstOrDefault(r => r.RequestUri?.AbsolutePath.EndsWith("device/latest", StringComparison.Ordinal) == true);
        Assert.NotNull(deviceRequest);

        var tokenRequest = Assert.Single(
            handler.CapturedRequests,
            r => r.Uri?.AbsolutePath.EndsWith("account/token", StringComparison.Ordinal) == true);
        var tokenBody = JsonSerializer.Deserialize<JsonElement>(tokenRequest.Body!);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            tokenBody.GetProperty("password").GetString());
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, object> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<CapturedRequest> CapturedRequests { get; } = new();

        public void AddResponse(string url, object response)
        {
            _responses[url] = response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(request);
            CapturedRequests.Add(new CapturedRequest(request.RequestUri, body));
            var url = request.RequestUri?.ToString() ?? "";
            
            // Try match exact URL or base URL
            var match = _responses.Keys.FirstOrDefault(k => url.StartsWith(k, StringComparison.Ordinal));
            
            if (match != null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(_responses[match])
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        public sealed record CapturedRequest(Uri? Uri, string? Body);
    }
}
