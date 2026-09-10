using GoldAiTrader.Core;
using GoldAiTrader.Risk;
using GoldAiTrader.Strategy;

namespace GoldAiTrader.Backtesting;

public sealed record BacktestOptions(decimal InitialBalance = 10_000m, decimal CommissionPerTrade = 0m,
    decimal Slippage = 0m, bool StopWinsSameBarAmbiguity = true);

public sealed record HistoricalFrame(MarketBar CompletedExecutionBar, MarketFeatures CompletedFeatures);
public sealed record BacktestTrade(DateTimeOffset EntryTime, DateTimeOffset ExitTime, TradeDirection Direction,
    decimal Entry, decimal Exit, decimal ProfitLoss, decimal RMultiple, string ExitReason);
public sealed record DecisionRecord(DateTimeOffset Time, StrategyAction Action, string Reason);
public sealed record BacktestResult(IReadOnlyList<BacktestTrade> Trades, IReadOnlyList<DecisionRecord> Decisions,
    IReadOnlyList<decimal> BalanceCurve, PerformanceMetrics Metrics);

public sealed record PerformanceMetrics(decimal NetReturnPercent, int TotalTrades, decimal ProfitFactor,
    decimal Expectancy, decimal WinRate, decimal AverageWin, decimal AverageLoss, decimal MaximumBalanceDrawdownPercent,
    int MaximumConsecutiveLosses, decimal RecoveryFactor);

public sealed class BacktestEngine(TrendPullbackV1 strategy, RiskEngine risk, SymbolSpecification symbol, BacktestOptions options)
{
    public BacktestResult Run(IReadOnlyList<HistoricalFrame> frames)
    {
        Validate(frames);
        var balance = options.InitialBalance;
        var peak = balance;
        var maxDrawdown = 0m;
        var trades = new List<BacktestTrade>();
        var decisions = new List<DecisionRecord>();
        var curve = new List<decimal> { balance };
        (TradeSetup Setup, decimal Volume)? position = null;
        var tradesToday = 0;
        DateOnly? day = null;
        DateTimeOffset? lastExit = null;

        foreach (var frame in frames)
        {
            var bar = frame.CompletedExecutionBar;
            var currentDay = DateOnly.FromDateTime(bar.OpenTime.UtcDateTime);
            if (day != currentDay) { day = currentDay; tradesToday = 0; }

            if (position is { } open)
            {
                var stopHit = open.Setup.Direction == TradeDirection.Buy ? bar.Low <= open.Setup.StopLoss : bar.High >= open.Setup.StopLoss;
                var targetHit = open.Setup.Direction == TradeDirection.Buy ? bar.High >= open.Setup.TakeProfit : bar.Low <= open.Setup.TakeProfit;
                if (stopHit || targetHit)
                {
                    var stopFirst = stopHit && (targetHit ? options.StopWinsSameBarAmbiguity : true);
                    var exit = stopFirst ? open.Setup.StopLoss : open.Setup.TakeProfit;
                    var points = open.Setup.Direction == TradeDirection.Buy ? exit - open.Setup.EntryPrice : open.Setup.EntryPrice - exit;
                    var pnl = points / symbol.TickSize * symbol.TickValuePerVolumeUnit * open.Volume - options.CommissionPerTrade - options.Slippage;
                    balance += pnl;
                    trades.Add(new(open.Setup.Time, bar.OpenTime, open.Setup.Direction, open.Setup.EntryPrice, exit, pnl,
                        points / open.Setup.StopDistance, stopFirst ? "StopLoss" : "TakeProfit"));
                    curve.Add(balance); peak = Math.Max(peak, balance);
                    maxDrawdown = Math.Max(maxDrawdown, RiskEngine.LossPercent(peak, balance));
                    position = null; lastExit = bar.OpenTime;
                }
            }

            var decision = strategy.Evaluate(frame.CompletedFeatures, new(tradesToday, position is not null, lastExit));
            decisions.Add(new(frame.CompletedFeatures.Time, decision.Action, decision.Reason));
            if (decision.Setup is null || position is not null) continue;
            var account = new AccountSnapshot(balance, balance, balance, balance, peak, peak, 0, 0);
            var assessment = risk.Assess(decision.Setup, account, symbol, frame.CompletedFeatures.Spread);
            if (!assessment.Approved) { decisions.Add(new(frame.CompletedFeatures.Time, StrategyAction.Wait, assessment.Reason)); continue; }
            position = (decision.Setup, assessment.PositionSize!.Volume); tradesToday++;
        }
        return new(trades, decisions, curve, CalculateMetrics(trades, options.InitialBalance, balance, maxDrawdown));
    }

    public static PerformanceMetrics CalculateMetrics(IReadOnlyList<BacktestTrade> trades, decimal initial, decimal final, decimal maxDrawdown)
    {
        var wins = trades.Where(t => t.ProfitLoss > 0).ToArray();
        var losses = trades.Where(t => t.ProfitLoss < 0).ToArray();
        var grossWin = wins.Sum(t => t.ProfitLoss); var grossLoss = Math.Abs(losses.Sum(t => t.ProfitLoss));
        var consecutive = 0; var maximumConsecutive = 0;
        foreach (var trade in trades) { consecutive = trade.ProfitLoss < 0 ? consecutive + 1 : 0; maximumConsecutive = Math.Max(maximumConsecutive, consecutive); }
        var net = final - initial;
        return new(initial == 0 ? 0 : net / initial * 100m, trades.Count,
            grossLoss == 0 ? (grossWin > 0 ? decimal.MaxValue : 0) : grossWin / grossLoss,
            trades.Count == 0 ? 0 : trades.Average(t => t.ProfitLoss),
            trades.Count == 0 ? 0 : 100m * wins.Length / trades.Count,
            wins.Length == 0 ? 0 : wins.Average(t => t.ProfitLoss),
            losses.Length == 0 ? 0 : losses.Average(t => t.ProfitLoss),
            maxDrawdown, maximumConsecutive, maxDrawdown == 0 ? 0 : net / (initial * maxDrawdown / 100m));
    }

    private static void Validate(IReadOnlyList<HistoricalFrame> frames)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            frames[i].CompletedExecutionBar.Validate();
            if (frames[i].CompletedFeatures.Time != frames[i].CompletedExecutionBar.OpenTime)
                throw new ArgumentException("Feature timestamp must match its completed execution bar.");
            if (i > 0 && frames[i].CompletedExecutionBar.OpenTime <= frames[i - 1].CompletedExecutionBar.OpenTime)
                throw new ArgumentException("Historical frames must be strictly chronological.");
        }
    }
}

public sealed record ChronologicalSplit<T>(IReadOnlyList<T> Development, IReadOnlyList<T> Validation, IReadOnlyList<T> OutOfSample);
public static class ResearchValidation
{
    public static ChronologicalSplit<T> Split<T>(IReadOnlyList<T> values, decimal developmentFraction = 0.60m, decimal validationFraction = 0.20m)
    {
        if (values.Count < 5 || developmentFraction <= 0 || validationFraction <= 0 || developmentFraction + validationFraction >= 1)
            throw new ArgumentException("Invalid chronological split.");
        var developmentEnd = (int)Math.Floor(values.Count * developmentFraction);
        var validationEnd = developmentEnd + (int)Math.Floor(values.Count * validationFraction);
        return new(values.Take(developmentEnd).ToArray(), values.Skip(developmentEnd).Take(validationEnd - developmentEnd).ToArray(), values.Skip(validationEnd).ToArray());
    }

    public static IEnumerable<BacktestOptions> StressScenarios(BacktestOptions baseline) =>
    [
        baseline,
        baseline with { CommissionPerTrade = baseline.CommissionPerTrade * 1.5m + 1m },
        baseline with { Slippage = baseline.Slippage * 2m + 0.25m }
    ];
}
