using BatteryEms.Application.Orchestration;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class DeviceWriteBrokerAttemptTests
{
    [Fact]
    public void Authority_requires_explicit_product_claim_and_forbids_one_on_legacy()
    {
        var request = Request();
        Assert.NotNull(request.EnsureValid());
        Assert.Throws<ArgumentException>(() => (request with { Authority = ActivationWriterAuthority.None }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (request with { Authority = ActivationWriterAuthority.ProductAgent }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (request with { ActivationClaimId = Guid.NewGuid() }).EnsureValid());
        Assert.NotNull((request with { Authority = ActivationWriterAuthority.ProductAgent, ActivationClaimId = Guid.NewGuid() }).EnsureValid());
    }

    [Theory]
    [InlineData("raw secret=example")]
    [InlineData("https://vendor.invalid")]
    [InlineData("aabbcc")]
    public void Hash_and_window_are_bounded_identity_fields_not_raw_vendor_payload(string value)
    {
        var request = Request();
        Assert.Throws<ArgumentException>(() => (request with { PayloadHash = value }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (request with { WindowId = value }).EnsureValid());
    }

    private static DeviceWriteBrokerBeginRequest Request() => new(Guid.NewGuid(), "site", new DateOnly(2026, 9, 23),
        "Z1", new string('A', 64), ActivationWriterAuthority.LegacyRunner, "writer", 1, 1, null,
        new DateTimeOffset(2026, 9, 22, 20, 55, 25, TimeSpan.Zero));
}
