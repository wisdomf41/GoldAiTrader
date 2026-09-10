using GoldAiTrader.MT5.BridgeHost;

namespace GoldAiTrader.Core.Tests;

public sealed class MT5BridgeReplayCacheTests
{
    [Fact]
    public void ReplayCacheIsBoundedAndRemovesExpiredEntries()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            AuthenticationTolerance = TimeSpan.FromSeconds(5),
            ReplayCacheCapacity = 1
        };
        var clock = new ManualBridgeTimeProvider(MT5BridgeHostTestFixture.InitialTime);
        var cache = new MT5RequestReplayCache(options, clock);

        Assert.Equal(MT5ReplayDecision.Accepted,
            cache.TryAccept("bridge-a", "nonce-000000000001", clock.GetUtcNow()));
        Assert.Equal(MT5ReplayDecision.CapacityExceeded,
            cache.TryAccept("bridge-a", "nonce-000000000002", clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(MT5ReplayDecision.Accepted,
            cache.TryAccept("bridge-a", "nonce-000000000003", clock.GetUtcNow()));
        Assert.Equal(1, cache.Count);
    }
}
