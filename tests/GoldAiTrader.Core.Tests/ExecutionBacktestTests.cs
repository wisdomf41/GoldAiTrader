using GoldAiTrader.Backtesting;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Risk;
using GoldAiTrader.Strategy;

namespace GoldAiTrader.Core.Tests;

public sealed class ExecutionBacktestTests
{
    [Fact]
    public async Task SimulatedExecutorPreventsDuplicateOrders()
    {
        var executor = new SimulatedTradeExecutor(new(ExecutionMode.Backtest));
        var order = new ApprovedOrder(Setup(), new PositionSize(1, 1));
        Assert.Equal(OrderStatus.Accepted, (await executor.SubmitAsync(order)).Status);
        Assert.Equal(OrderStatus.Duplicate, (await executor.SubmitAsync(order)).Status);
    }

    [Fact]
    public void LiveExecutionConfigurationIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => new ExecutionOptions(ExecutionMode.Live, false).Validate());

    [Fact]
    public void ChronologicalSplitNeverInterleavesPeriods()
    {
        var split = ResearchValidation.Split(Enumerable.Range(0, 10).ToArray());
        Assert.Equal([0, 1, 2, 3, 4, 5], split.Development);
        Assert.Equal([6, 7], split.Validation);
        Assert.Equal([8, 9], split.OutOfSample);
    }

    [Fact]
    public void BacktestRejectsNonChronologicalFrames()
    {
        var time = DateTimeOffset.Parse("2026-01-05T14:00:00Z");
        var frame = Frame(time);
        var engine = Engine();
        Assert.Throws<ArgumentException>(() => engine.Run([frame, frame]));
    }

    [Fact]
    public void SameBarAmbiguityExecutesStopConservatively()
    {
        var time = DateTimeOffset.Parse("2026-01-05T14:00:00Z");
        var entry = Frame(time);
        var exitBar = new MarketBar(time.AddMinutes(5), 2000m, 2010m, 1990m, 2000m, 1999.9m, 2000m);
        var exit = new HistoricalFrame(exitBar, Features(time.AddMinutes(5)));
        var result = Engine().Run([entry, exit]);
        Assert.Single(result.Trades);
        Assert.Equal("StopLoss", result.Trades[0].ExitReason);
        Assert.True(result.Trades[0].ProfitLoss < 0);
    }

    private static BacktestEngine Engine() => new(new TrendPullbackV1(new()), new RiskEngine(new()),
        new SymbolSpecification(0.1m, 0.1m, 1m, 100m, 1m, 0.1m), new());
    private static TradeSetup Setup() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, "XAUUSD", TradeDirection.Buy,
        2000m, 1999m, 2002m, "test", "test");
    private static HistoricalFrame Frame(DateTimeOffset time) =>
        new(new MarketBar(time, 2000m, 2001m, 1999m, 2000m, 2000m, 2000.1m), Features(time));
    private static MarketFeatures Features(DateTimeOffset time) => new(time, "XAUUSD", 2000m, 2000.1m,
        2000m, 1999m, 2001m, 1990m, 2m, 60m, 30m, 0.01m,
        MarketSession.LondonNewYorkOverlap, MarketRegime.Trending);
}
