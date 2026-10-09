using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Orchestration;
using Xunit;

namespace BatteryEms.Application.Tests;

public sealed class ActivationPrewriteClaimTests
{
    private static readonly JsonSerializerOptions CachedJson = new() { WriteIndented = true };
    private static readonly DateTimeOffset Now =
        new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Claim_is_bounded_to_thirty_seconds()
    {
        var claim = Claim();

        Assert.NotNull(claim.EnsureValid());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (claim with { ExpiresAtUtc = Now.AddSeconds(31) }).EnsureValid());
    }

    [Fact]
    public void Payload_integrity_canonicalizes_jsonb_formatting()
    {
        var snapshot = Snapshot();
        var canonical = JsonSerializer.Serialize(snapshot);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var reformatted = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(canonical),
            CachedJson);

        Assert.True(ActivationPayloadIntegrity.IsValid(reformatted, hash));
        Assert.False(ActivationPayloadIntegrity.IsValid(reformatted, new string('0', 64)));
        Assert.False(ActivationPayloadIntegrity.IsValid("{}", hash));
    }

    [Fact]
    public async Task In_memory_claim_and_completion_fail_closed()
    {
        var store = new FailClosedActivationPrewriteClaimStore();
        var claim = await store.TryClaimAsync(
            new ActivationPrewriteClaimRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "executor-a",
                "writer-a",
                2,
                7,
                Now),
            CancellationToken.None);
        var completion = await store.CompleteAsync(
            new ActivationPrewriteCompletionRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "executor-a",
                Succeeded: false,
                "not-written",
                Now),
            CancellationToken.None);

        Assert.False(claim.Claimed);
        Assert.False(completion.Completed);
        Assert.Equal("activation-durable-persistence-required", claim.ErrorCode);
        Assert.Equal("activation-durable-persistence-required", completion.ErrorCode);
    }

    [Theory]
    [InlineData("raw adapter error")]
    [InlineData("https://vendor.invalid")]
    [InlineData("secret=example")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("UpperCase")]
    public void Completion_rejects_unbounded_or_raw_outcome_text(string code)
    {
        var request = new ActivationPrewriteCompletionRequest(
            Guid.NewGuid(), Guid.NewGuid(), "executor-a", false, code, Now);
        Assert.Throws<ArgumentException>(() => request.EnsureValid());
        Assert.Throws<ArgumentException>(() => (request with { OutcomeCode = new string('a', 97) }).EnsureValid());
        Assert.NotNull((request with { OutcomeCode = new string('a', 96) }).EnsureValid());
    }

    private static ActivationPrewriteClaim Claim() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "site-1",
        "Z1",
        "window-hash",
        "executor-a",
        "writer-a",
        2,
        7,
        "hash",
        "{}",
        Now,
        Now.AddSeconds(30));

    private static ShadowPlanSnapshot Snapshot()
    {
        var intervals = new[]
        {
            new ShadowTouInterval("00:00", true, false, false, 0, 30, 290),
            new ShadowTouInterval("01:00", true, true, false, 80000, 100, 290),
            new ShadowTouInterval("02:00", true, false, false, 0, 100, 290),
            new ShadowTouInterval("03:00", true, false, true, 80000, 30, 290),
            new ShadowTouInterval("04:00", true, false, false, 0, 30, 290),
            new ShadowTouInterval("05:00", true, false, false, 0, 30, 290),
        };
        return new ShadowPlanSnapshot(
            "site-1",
            new DateOnly(2026, 9, 29),
            PayloadReady: true,
            [],
            [
                new ShadowTouWindow("Z1", intervals),
                new ShadowTouWindow("Z2", intervals),
                new ShadowTouWindow("Z3", intervals),
                new ShadowTouWindow("Z4", intervals),
            ]).EnsureValid();
    }
}
