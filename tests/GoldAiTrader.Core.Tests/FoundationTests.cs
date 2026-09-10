using GoldAiTrader.Core;
using GoldAiTrader.Market;
using GoldAiTrader.Risk;
using GoldAiTrader.Strategy;

namespace GoldAiTrader.Core.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void MarketBarRejectsInvalidRelationship() =>
        Assert.Throws<ArgumentException>(() => new MarketBar(DateTimeOffset.UtcNow, 10, 9, 8, 9, 9, 10).Validate());

    [Fact]
    public void IndicatorsRejectNonChronologicalBars()
    {
        var timestamp = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => Indicators.Atr([Bar(timestamp), Bar(timestamp)], 1));
    }

    [Fact] public void EmaUsesSuppliedValues() => Assert.Equal(12m, Indicators.Ema([10m, 11m, 12m, 13m], 3));
    [Fact] public void RsiHandlesNoLosses() => Assert.Equal(100m, Indicators.Rsi([1m, 2m, 3m], 2));

    [Fact]
    public void RiskEngineUsesHalfPercentEquityAndNormalizesDown()
    {
        var result = new RiskEngine(new()).Assess(Setup(), Account(), Symbol(), 0.1m);
        Assert.True(result.Approved);
        Assert.Equal(5m, result.PositionSize!.PlannedRiskAmount);
        Assert.Equal(5m, result.PositionSize.Volume);
    }

    [Fact]
    public void RiskEngineRejectsDailyHardStopBoundary()
    {
        var result = new RiskEngine(new()).Assess(Setup(), Account() with { Equity = 980m }, Symbol(), 0.1m);
        Assert.False(result.Approved);
        Assert.Contains("Daily", result.Reason);
    }

    [Fact]
    public void RiskEngineRejectsMinimumVolumeThatWouldExceedRisk() =>
        Assert.False(new RiskEngine(new()).Assess(Setup(), Account(), Symbol() with { MinVolume = 10m }, 0.1m).Approved);

    [Fact]
    public void RiskEngineRejectsOpenPositionLimit() =>
        Assert.False(new RiskEngine(new()).Assess(Setup(), Account() with { OpenPositions = 1 }, Symbol(), 0.1m).Approved);

    [Fact]
    public void RiskEngineRejectsEmergencyShutdown() =>
        Assert.False(new RiskEngine(new()).Assess(Setup(), Account() with { EmergencyShutdown = true }, Symbol(), 0.1m).Approved);

    [Fact]
    public void StrategyProducesTwoRBuyCandidate()
    {
        var decision = new TrendPullbackV1(new()).Evaluate(Features(), new(0, false, null));
        Assert.Equal(StrategyAction.Buy, decision.Action);
        Assert.Equal(2m, decision.Setup!.RewardDistance / decision.Setup.StopDistance);
    }

    [Fact]
    public void StrategyWaitsWhenCooldownActive()
    {
        var features = Features();
        var decision = new TrendPullbackV1(new()).Evaluate(features, new(0, false, features.Time.AddMinutes(-5)));
        Assert.Equal(StrategyAction.Wait, decision.Action);
    }

    [Fact]
    public void StrategyWaitsOutsideSession()
    {
        var decision = new TrendPullbackV1(new()).Evaluate(Features() with { Session = MarketSession.Closed }, new(0, false, null));
        Assert.Equal(StrategyAction.Wait, decision.Action);
    }

    private static MarketBar Bar(DateTimeOffset time) => new(time, 10, 11, 9, 10, 9.9m, 10m);
    private static AccountSnapshot Account() => new(1000, 1000, 1000, 1000, 1000, 1000, 0, 0);
    private static SymbolSpecification Symbol() => new(0.1m, 0.1m, 1m, 100m, 1m, 0.1m);
    private static TradeSetup Setup() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, "XAUUSD", TradeDirection.Buy, 2000m, 1999m, 2002m, "test", "test");
    private static MarketFeatures Features() => new(DateTimeOffset.Parse("2026-01-05T14:00:00Z"), "XAUUSD",
        2000m, 2000.1m, 2000m, 1999m, 2001m, 1990m, 2m, 60m, 30m, 0.01m,
        MarketSession.LondonNewYorkOverlap, MarketRegime.Trending);
}
