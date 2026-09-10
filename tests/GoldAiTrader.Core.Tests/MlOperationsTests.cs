using GoldAiTrader.Bot;
using GoldAiTrader.Execution;
using GoldAiTrader.ML;

namespace GoldAiTrader.Core.Tests;

public sealed class MlOperationsTests
{
    [Fact]
    public void MlTrainingRejectsFutureLeakage()
    {
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var rows = Enumerable.Range(0, 10).Select(i => Row(start.AddDays(i), i % 2 == 0)).ToArray();
        Assert.Throws<InvalidOperationException>(() => new MlTradeQualityFilter().Train(rows, start.AddDays(8), "v1"));
    }

    [Fact]
    public void WalkForwardUsesOnlyEarlierTrainingRows()
    {
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var rows = Enumerable.Range(0, 20).Select(i => Row(start.AddDays(i), i % 2 == 0)).ToArray();
        var folds = WalkForwardEvaluator.CreateFolds(rows, 10, 5);
        Assert.All(folds, fold => Assert.True(fold.TrainingThrough < fold.TestFrom));
        Assert.Equal(2, folds.Count);
    }

    [Fact]
    public void MlDisabledModeAlwaysLeavesDeterministicCandidateEligible()
    {
        var prediction = new MlTradeQualityFilter().Predict(Row(DateTimeOffset.UtcNow, true), new());
        Assert.True(prediction.Approved);
        Assert.Equal("disabled", prediction.ModelVersion);
    }

    [Fact]
    public void DefaultEnvironmentNeverAcceptsOrders()
    {
        var options = new TradingEnvironmentOptions();
        var guard = new OperationalGuard(options);
        guard.SetBrokerConnected(true);
        guard.RecordMarketData(DateTimeOffset.UtcNow);
        Assert.False(guard.GetStatus(DateTimeOffset.UtcNow).AcceptingNewTrades);
    }

    [Fact]
    public void StaleDataAndEmergencyShutdownFailClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var guard = new OperationalGuard(new(ExecutionMode.Demo, EnableTrading: true));
        guard.SetBrokerConnected(true);
        guard.RecordMarketData(now.AddMinutes(-11));
        Assert.False(guard.GetStatus(now).AcceptingNewTrades);
        guard.RecordMarketData(now);
        guard.TriggerEmergencyShutdown();
        Assert.False(guard.GetStatus(now).AcceptingNewTrades);
    }

    [Fact]
    public void StartupValidatorRejectsLiveMode()
    {
        var errors = new StartupValidator().Validate(new(ExecutionMode.Live, EnableLiveTrading: true),
            new(ExecutionMode.Live, DemoOnly: false));
        Assert.NotEmpty(errors);
    }

    private static TradeFeatureRow Row(DateTimeOffset time, bool label) =>
        new(time, 1, 50, 25, 0.1f, 0.01f, 1, label, "synthetic-v1");
}
