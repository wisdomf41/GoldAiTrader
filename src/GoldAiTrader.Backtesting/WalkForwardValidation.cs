using System.Text.Json;
using GoldAiTrader.Core;

namespace GoldAiTrader.Backtesting;

public sealed record ResearchWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record WalkForwardFold(ResearchWindow Training, ResearchWindow Validation, ResearchWindow OutOfSample);
public sealed record FoldReport(WalkForwardFold Windows, ResearchRun Validation, ResearchRun OutOfSample,
    ResearchRun Baseline, ResearchRun HigherCosts, ResearchRun ParameterSensitivity);
public sealed record ValidationPolicy(int MinimumFolds = 3, int MinimumTradesPerFold = 30,
    decimal MinimumProfitFactor = 1.2m, decimal MinimumExpectancy = 0,
    decimal MaximumDrawdownPercent = 12, decimal MaximumSingleFoldProfitShare = .6m,
    decimal MaximumSingleTradeProfitShare = .2m, bool RequireBaselineImprovement = true)
{
    public void Validate()
    {
        if (MinimumFolds < 2 || MinimumTradesPerFold < 1 || MinimumProfitFactor < 1 ||
            MaximumDrawdownPercent is <= 0 or > 100 || MaximumSingleFoldProfitShare is <= 0 or > 1 ||
            MaximumSingleTradeProfitShare is <= 0 or > 1)
            throw new ArgumentException("Invalid validation policy.");
    }
}
public sealed record ValidationGates(bool DataIntegrity, bool LeakageAndReproducibility, bool RiskLimits,
    bool StrategyPerformance, IReadOnlyList<string> DataFailures, IReadOnlyList<string> MethodFailures,
    IReadOnlyList<string> RiskFailures, IReadOnlyList<string> PerformanceFailures)
{
    public bool Passed => DataIntegrity && LeakageAndReproducibility && RiskLimits && StrategyPerformance;
}
public sealed record RobustnessReport(decimal FoldReturnRange, decimal? LargestFoldGrossProfitShare,
    decimal? LargestTradeGrossProfitShare, decimal? TopFiveTradeGrossProfitShare,
    bool SufficientSample, IReadOnlyList<decimal> ValidationCostStressReturns,
    IReadOnlyList<decimal> ValidationParameterStressReturns);
public sealed record WalkForwardReport(int FormatVersion, ValidationPolicy Policy, IReadOnlyList<FoldReport> Folds,
    ResearchMetrics? Aggregate, RobustnessReport? Robustness, ValidationGates Gates);

public static class WalkForwardValidation
{
    public static IReadOnlyList<WalkForwardFold> Plan(ResearchDataset dataset, int trainingBars,
        int validationBars, int outOfSampleBars)
    {
        dataset.Validate();
        if (trainingBars < 600 || validationBars < 1 || outOfSampleBars < 1)
            throw new ArgumentException("Training must provide at least 600 M5 warmup bars.");
        var bars = dataset.Bars.Where(x => x.TimeframeMinutes == 5).ToArray();
        var size = checked(trainingBars + validationBars + outOfSampleBars);
        var folds = new List<WalkForwardFold>();
        for (var i = 0; i <= bars.Length - size; i += size)
        {
            folds.Add(new(new(bars[i].OpenTimeUtc, bars[i + trainingBars].OpenTimeUtc),
                new(bars[i + trainingBars].OpenTimeUtc, bars[i + trainingBars + validationBars].OpenTimeUtc),
                new(bars[i + trainingBars + validationBars].OpenTimeUtc, bars[i + size - 1].CloseTimeUtc)));
        }
        return folds;
    }

    public static WalkForwardReport Run(ResearchDataset dataset, ResearchRunSettings settings,
        IReadOnlyList<WalkForwardFold> folds, ValidationPolicy? policy = null)
    {
        policy ??= new();
        policy.Validate();
        settings.Validate();
        try { dataset.Validate(); }
        catch (ArgumentException)
        {
            return Failed("DatasetInvalid", true);
        }
        DateTimeOffset? priorEnd = null;
        foreach (var fold in folds)
        {
            var windows = new[] { fold.Training, fold.Validation, fold.OutOfSample };
            if (windows.Any(w => !ResearchDataset.Utc(w.StartUtc) || !ResearchDataset.Utc(w.EndUtc) || w.StartUtc >= w.EndUtc) ||
                fold.Training.EndUtc != fold.Validation.StartUtc || fold.Validation.EndUtc != fold.OutOfSample.StartUtc ||
                fold.OutOfSample.EndUtc > dataset.AsOfUtc || priorEnd > fold.Training.StartUtc)
                return Failed("OverlappingOrNoncausalWindows", false);
            priorEnd = fold.OutOfSample.EndUtc;
        }
        var reports = new List<FoldReport>();
        var reproducible = true;
        foreach (var fold in folds)
        {
            // Each fold is independent. Only historical candles from its own training window warm indicators.
            var input = dataset with { Bars = dataset.Bars.Where(b => b.OpenTimeUtc >= fold.Training.StartUtc &&
                b.CloseTimeUtc <= fold.OutOfSample.EndUtc).ToArray() };
            if (input.Bars.Count == 0) return Failed("WindowCoverage", true);
            var validation = DatasetBacktest.Run(input, settings, fold.Validation.StartUtc, fold.Validation.EndUtc);
            var test = DatasetBacktest.Run(input, settings, fold.OutOfSample.StartUtc, fold.OutOfSample.EndUtc);
            var repeat = DatasetBacktest.Run(input, settings, fold.OutOfSample.StartUtc, fold.OutOfSample.EndUtc);
            reproducible &= JsonSerializer.Serialize(test) == JsonSerializer.Serialize(repeat);
            reports.Add(new(fold, validation, test,
                DatasetBacktest.Run(input, settings, fold.OutOfSample.StartUtc, fold.OutOfSample.EndUtc, true),
                DatasetBacktest.Run(input, settings with { Costs = settings.Costs with
                    { SpreadMultiplier = settings.Costs.SpreadMultiplier * 1.5m,
                        CommissionPerLotPerSide = settings.Costs.CommissionPerLotPerSide * 1.5m,
                        SlippagePricePerSide = settings.Costs.SlippagePricePerSide * 1.5m } },
                    fold.Validation.StartUtc, fold.Validation.EndUtc),
                DatasetBacktest.Run(input, settings with { Strategy = settings.Strategy with
                    { PullbackAtrTolerance = settings.Strategy.PullbackAtrTolerance * .9m,
                        StopAtrMultiple = settings.Strategy.StopAtrMultiple * 1.1m } },
                    fold.Validation.StartUtc, fold.Validation.EndUtc)));
        }
        var tests = reports.Select(r => r.OutOfSample).ToArray();
        var allTrades = tests.SelectMany(r => r.Trades).ToArray();
        ResearchMetrics? aggregate = null;
        if (tests.Length > 0)
        {
            // Equal independently funded sleeves: no fictitious compounding across disconnected folds.
            var capital = settings.InitialCapital * tests.Length;
            decimal cumulative = 0;
            var equity = new List<decimal> { capital };
            foreach (var run in tests)
            {
                equity.AddRange(run.Equity.Select(e => capital + cumulative + e - settings.InitialCapital));
                cumulative += run.Metrics.NetProfit;
            }
            aggregate = ResearchMetrics.Calculate(capital, allTrades, equity, tests.Sum(r => r.ObservedBars) == 0 ? 0 :
                tests.Sum(r => r.Metrics.ExposurePercent * r.ObservedBars) / tests.Sum(r => r.ObservedBars));
        }
        var sufficient = tests.Length >= policy.MinimumFolds && tests.All(r => r.Metrics.TradeCount >= policy.MinimumTradesPerFold);
        var gross = tests.Sum(r => r.Metrics.GrossProfit);
        var profits = allTrades.Where(t => t.ProfitLoss > 0).Select(t => t.ProfitLoss).OrderDescending().ToArray();
        var robustness = new RobustnessReport(tests.Length == 0 ? 0 :
            tests.Max(r => r.Metrics.ReturnPercent) - tests.Min(r => r.Metrics.ReturnPercent),
            sufficient && gross > 0 ? tests.Max(r => r.Metrics.GrossProfit) / gross : null,
            sufficient && gross > 0 ? profits.First() / gross : null,
            sufficient && gross > 0 ? profits.Take(5).Sum() / gross : null,
            sufficient, reports.Select(r => r.HigherCosts.Metrics.ReturnPercent).ToArray(),
            reports.Select(r => r.ParameterSensitivity.Metrics.ReturnPercent).ToArray());
        var data = reports.SelectMany(r => new[] { r.Validation, r.OutOfSample, r.Baseline, r.HigherCosts, r.ParameterSensitivity })
            .SelectMany(r => r.DataFailures).Distinct().ToList();
        if (reports.Count == 0) data.Add("NoFolds");
        var method = reproducible ? new List<string>() : ["NonReproducible"];
        if (dataset.Source == ResearchSource.SyntheticFixture) method.Add("SyntheticEvidenceOnly");
        if (!sufficient) method.Add("InsufficientEvidence");
        var riskFailures = reports.SelectMany(r => new[] { r.Validation, r.OutOfSample, r.HigherCosts, r.ParameterSensitivity })
            .SelectMany(r => r.RiskFailures).Distinct().ToList();
        if (tests.Any(r => r.Metrics.MaximumDrawdownPercent > Math.Min(policy.MaximumDrawdownPercent, settings.Risk.HardDrawdownPercent)))
            riskFailures.Add("DrawdownPolicy");
        var performance = new List<string>();
        if (!sufficient) performance.Add("InsufficientTradesOrFolds");
        if (reports.Any(r => !AcceptPerformance(r.Validation.Metrics, policy) || !AcceptPerformance(r.OutOfSample.Metrics, policy)))
            performance.Add("ProfitabilityThreshold");
        if (policy.RequireBaselineImprovement && reports.Any(r => r.OutOfSample.Metrics.NetProfit <= r.Baseline.Metrics.NetProfit))
            performance.Add("BaselineNotExceeded");
        if (reports.Any(r => r.HigherCosts.Metrics.NetProfit <= 0 || r.ParameterSensitivity.Metrics.NetProfit <= 0))
            performance.Add("ValidationSensitivity");
        if (robustness.LargestFoldGrossProfitShare > policy.MaximumSingleFoldProfitShare ||
            robustness.LargestTradeGrossProfitShare > policy.MaximumSingleTradeProfitShare)
            performance.Add("ProfitConcentration");
        return new(1, policy, reports, aggregate, robustness, new(data.Count == 0, method.Count == 0,
            riskFailures.Count == 0, performance.Count == 0, data, method, riskFailures, performance));

        WalkForwardReport Failed(string reason, bool dataFailure) =>
            new(1, policy, [], null, null, new(!dataFailure, false, false, false,
                dataFailure ? [reason] : [], dataFailure ? [] : [reason], ["NotEvaluated"], ["NotEvaluated"]));
    }

    public static bool AcceptPerformance(ResearchMetrics metrics, ValidationPolicy policy) =>
        metrics.TradeCount >= policy.MinimumTradesPerFold && metrics.Expectancy > policy.MinimumExpectancy &&
        (metrics.ProfitFactorUnbounded || metrics.ProfitFactor >= policy.MinimumProfitFactor);
}
