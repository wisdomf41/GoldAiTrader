using GoldAiTrader.Core;
using GoldAiTrader.Risk;
using GoldAiTrader.Strategy;

namespace GoldAiTrader.Core.Tests;

public sealed class RiskHardeningTests
{
    public static TheoryData<RiskOptions> InvalidOptions => new()
    {
        new(RiskPerTradePercent: 0), new(MaxTotalRiskPercent: 0),
        new(RiskPerTradePercent: 1, MaxTotalRiskPercent: 0.5m), new(MaxOpenPositions: 0),
        new(MaxSpread: -0.01m), new(DailyWarningPercent: 0), new(DailyHardStopPercent: 0),
        new(DailyWarningPercent: 2m, DailyHardStopPercent: 2m), new(WeeklyHardStopPercent: 0),
        new(DrawdownWarningPercent: 0), new(HardDrawdownPercent: 0),
        new(DrawdownWarningPercent: 12m, HardDrawdownPercent: 12m),
        new(RiskPerTradePercent: 101m, MaxTotalRiskPercent: 101m),
        new(MaxTotalRiskPercent: 101m), new(WeeklyHardStopPercent: 101m),
        new(HardDrawdownPercent: 101m), new(ConsecutiveLossLimit: 0),
        new(ConsecutiveLossCooldown: TimeSpan.Zero)
    };

    [Theory, MemberData(nameof(InvalidOptions))]
    public void InvalidRiskOptionsAreRejected(RiskOptions options) =>
        Assert.ThrowsAny<ArgumentException>(() => new RiskEngine(options));

    [Theory]
    [InlineData(980.1, true, true)]
    [InlineData(980.0, false, false)]
    [InlineData(979.9, false, false)]
    public void DailyLossBoundaries(decimal equity, bool approved, bool warning)
    {
        var result = Assess(Account(equity: equity));
        Assert.Equal(approved, result.Approved);
        if (approved) Assert.Equal(warning, result.Warning);
    }

    [Fact]
    public void DailyWarningStartsExactlyAtOnePointFivePercent()
    {
        Assert.False(Assess(Account(equity: 985.1m)).Warning);
        Assert.True(Assess(Account(equity: 985m)).Warning);
    }

    [Theory]
    [InlineData(960.1, true)]
    [InlineData(960.0, false)]
    [InlineData(959.9, false)]
    public void WeeklyLossBoundaries(decimal equity, bool approved)
    {
        var account = Account(equity: equity) with { DayStartEquity = equity, WeekStartEquity = 1000m };
        Assert.Equal(approved, Assess(account).Approved);
    }

    [Theory]
    [InlineData(false, 920.1, true, false)]
    [InlineData(false, 920.0, true, true)]
    [InlineData(false, 880.1, true, true)]
    [InlineData(false, 880.0, false, false)]
    [InlineData(false, 879.9, false, false)]
    [InlineData(true, 920.1, true, false)]
    [InlineData(true, 920.0, true, true)]
    [InlineData(true, 880.1, true, true)]
    [InlineData(true, 880.0, false, false)]
    [InlineData(true, 879.9, false, false)]
    public void EquityAndBalanceDrawdownBoundaries(bool balanceDrawdown, decimal current, bool approved, bool warning)
    {
        var account = balanceDrawdown
            ? Account() with { Balance = current, PeakBalance = 1000m }
            : Account(equity: current) with { DayStartEquity = current, WeekStartEquity = current, PeakEquity = 1000m };
        var result = Assess(account);
        Assert.Equal(approved, result.Approved);
        if (approved) Assert.Equal(warning, result.Warning);
    }

    [Fact]
    public void TotalExposureAcceptsExactBoundaryAndRejectsAboveIt()
    {
        var engine = new RiskEngine(new(MaxTotalRiskPercent: 1m));
        Assert.True(engine.Assess(Setup(), Account() with { OpenRiskAmount = 5m }, Symbol(), 0.1m).Approved);
        Assert.False(engine.Assess(Setup(), Account() with { OpenRiskAmount = 5.01m }, Symbol(), 0.1m).Approved);
    }

    [Theory]
    [InlineData(0.49, true)]
    [InlineData(0.50, true)]
    [InlineData(0.51, false)]
    [InlineData(-0.01, false)]
    public void SpreadBoundaries(decimal spread, bool approved) =>
        Assert.Equal(approved, Assess(Account(), spread: spread).Approved);

    [Fact]
    public void VolumeRoundsDownWithoutIncreasingRisk()
    {
        var result = Assess(Account(), setup: Setup(stopDistance: 0.6m));
        Assert.Equal(8m, result.PositionSize!.Volume);
        Assert.Equal(4.8m, result.PositionSize.PlannedRiskAmount);
    }

    [Fact]
    public void MinimumAndMaximumBrokerVolumesAreRespected()
    {
        Assert.Equal(5m, Assess(Account(), symbol: Symbol() with { MinVolume = 5m }).PositionSize!.Volume);
        Assert.Equal(2m, Assess(Account(), symbol: Symbol() with { MaxVolume = 2m }).PositionSize!.Volume);
        Assert.False(Assess(Account(), symbol: Symbol() with { MinVolume = 6m }).Approved);
    }

    [Fact]
    public void ZeroBrokerMinimumStopIsValidButZeroTradeStopIsRejected()
    {
        var symbol = Symbol() with { MinStopDistance = 0m };
        Assert.True(Assess(Account(), symbol: symbol).Approved);
        Assert.False(Assess(Account(), symbol: symbol, setup: Setup(stopDistance: 0m)).Approved);
    }

    [Fact]
    public void ConsecutiveLossesSuspendAndNeverIncreaseRisk()
    {
        var options = new RiskOptions();
        var time = DateTimeOffset.Parse("2026-01-05T12:00:00Z");
        var firstSize = new RiskEngine(options).Assess(Setup(time: time), Account(), Symbol(), 0.1m).PositionSize!;
        var state = RiskStateTracker.RecordClosedTrade(new(), -1m, time, options);
        var secondSize = new RiskEngine(options).Assess(Setup(time: time.AddMinutes(1)), Account(), Symbol(), 0.1m, state).PositionSize!;
        state = RiskStateTracker.RecordClosedTrade(state, -1m, time.AddMinutes(2), options);
        state = RiskStateTracker.RecordClosedTrade(state, -1m, time.AddMinutes(3), options);

        Assert.Equal(firstSize, secondSize);
        Assert.False(new RiskEngine(options).Assess(Setup(time: time.AddMinutes(4)), Account(), Symbol(), 0.1m, state, time.AddMinutes(4)).Approved);
        Assert.True(new RiskEngine(options).Assess(Setup(time: state.SuspendedUntil), Account(), Symbol(), 0.1m, state, state.SuspendedUntil).Approved);
        Assert.Equal(new RiskState(), RiskStateTracker.RecordClosedTrade(state, 1m, time.AddHours(5), options));
    }

    [Fact]
    public void InvalidInputsFailClosed()
    {
        Assert.False(Assess(Account(equity: 0m)).Approved);
        Assert.False(Assess(Account(equity: -1m)).Approved);
        Assert.False(Assess(Account(), symbol: Symbol() with { TickSize = 0m }).Approved);
        Assert.False(Assess(Account(), symbol: Symbol() with { IsTradingAvailable = false }).Approved);
        Assert.False(Assess(Account(), symbol: Symbol() with { CanonicalSymbol = "XAGUSD" }).Approved);
        Assert.False(Assess(Account(), setup: Setup() with { StopLoss = 2001m }).Approved);
        Assert.False(Assess(Account(), setup: Setup() with { TakeProfit = 1999m }).Approved);
        Assert.False(Assess(Account(), setup: Setup(TradeDirection.Sell) with { StopLoss = 1999m }).Approved);
        Assert.False(Assess(Account(), setup: Setup(TradeDirection.Sell) with { TakeProfit = 2001m }).Approved);
    }

    [Fact]
    public void StrategyAppliesAtrRelativeSpreadFilterOutsideRiskEngine()
    {
        var allowed = Features(0.20m);
        var rejected = Features(0.21m);
        var strategy = new TrendPullbackV1(new());
        Assert.Equal(StrategyAction.Buy, strategy.Evaluate(allowed, new(0, false, null)).Action);
        Assert.Equal(StrategyAction.Wait, strategy.Evaluate(rejected, new(0, false, null)).Action);
    }

    private static RiskAssessment Assess(AccountSnapshot account, decimal spread = 0.1m,
        SymbolSpecification? symbol = null, TradeSetup? setup = null) =>
        new RiskEngine(new()).Assess(setup ?? Setup(), account, symbol ?? Symbol(), spread);

    private static AccountSnapshot Account(decimal equity = 1000m) =>
        new(1000m, equity, 1000m, 1000m, 1000m, 1000m, 0, 0m);

    private static SymbolSpecification Symbol() =>
        new(0.1m, 0.1m, 1m, 100m, 1m, 0.1m, "GOLD", "XAUUSD", 100m, true);

    private static TradeSetup Setup(TradeDirection direction = TradeDirection.Buy, decimal stopDistance = 1m,
        DateTimeOffset? time = null) => direction == TradeDirection.Buy
        ? new(Guid.NewGuid(), time ?? DateTimeOffset.UtcNow, "XAUUSD", direction, 2000m, 2000m - stopDistance, 2002m, "test", "test")
        : new(Guid.NewGuid(), time ?? DateTimeOffset.UtcNow, "XAUUSD", direction, 2000m, 2000m + stopDistance, 1998m, "test", "test");

    private static MarketFeatures Features(decimal spread) => new(DateTimeOffset.Parse("2026-01-05T14:00:00Z"),
        "XAUUSD", 2000m, 2000m + spread, 2000m, 1999m, 2001m, 1990m, 2m, 60m, 30m, 0.01m,
        MarketSession.LondonNewYorkOverlap, MarketRegime.Trending);
}
