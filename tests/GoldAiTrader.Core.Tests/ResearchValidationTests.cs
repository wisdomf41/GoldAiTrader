using System.Text;
using System.Text.Json;
using GoldAiTrader.Backtesting;
using GoldAiTrader.Core;
using GoldAiTrader.Data;
using GoldAiTrader.Risk;
using GoldAiTrader.Strategy;

namespace GoldAiTrader.Core.Tests;

public sealed class ResearchValidationTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2025-01-06T00:00:00Z");
    public static ResearchDataset Dataset(int count = 660)
    {
        var bars = new List<ResearchBar>();
        for (var i = 0; i < count; i++)
        {
            var time = Epoch.AddMinutes(5 * i);
            var price = 2000m + i * .05m + (i % 4 - 2) * .03m;
            bars.Add(new(time, time.AddMinutes(5), time.AddMinutes(5), 5,
                price, price + .5m, price - .5m, price + .02m, 100, .1m));
            if (i % 3 == 2)
                bars.Add(new(time.AddMinutes(-10), time.AddMinutes(5), time.AddMinutes(5), 15,
                    price - .1m, price + .5m, price - .6m, price + .02m, 300, .1m));
        }
        return new(1, "XAUUSD", "XAUUSD.demo", ResearchSource.HistoricalImport, Epoch.AddMinutes(5 * count), bars);
    }
    private static ResearchRunSettings Settings() => new(10000,
        new(.01m, 1m, .01m, 100m, .01m, .1m, "XAUUSD.demo", "XAUUSD", 100),
        new(.2m, 3m, .02m), new(), new());
    private static ResearchTrade Trade(decimal pnl, int offset = 0) =>
        new(Epoch.AddMinutes(offset), Epoch.AddMinutes(offset + 5), TradeDirection.Buy, 2000, 2001, 1, pnl, pnl / 10, "Test");

    [Fact] public void DatasetRoundTripIsDeterministic()
    {
        var bytes = ResearchDatasetFile.Serialize(Dataset());
        Assert.Equal(bytes, ResearchDatasetFile.Serialize(ResearchDatasetFile.Load(bytes)));
        Assert.Equal(ResearchDatasetFile.Fingerprint(Dataset()), ResearchDatasetFile.Fingerprint(Dataset()));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("conflict")]
    [InlineData("ordering")]
    [InlineData("ohlc")]
    [InlineData("utc")]
    [InlineData("future")]
    [InlineData("incomplete")]
    [InlineData("volume")]
    [InlineData("spread")]
    [InlineData("timeframe")]
    public void InvalidDatasetIsRejected(string error)
    {
        var dataset = Dataset(6);
        var bars = dataset.Bars.ToList();
        switch (error)
        {
            case "duplicate": bars.Insert(1, bars[0]); break;
            case "conflict": bars.Insert(1, bars[0] with { Close = bars[0].Close + .01m }); break;
            case "ordering": bars.Reverse(); break;
            case "ohlc": bars[0] = bars[0] with { High = 1 }; break;
            case "utc": bars[0] = bars[0] with { OpenTimeUtc = bars[0].OpenTimeUtc.ToOffset(TimeSpan.FromHours(1)) }; break;
            case "future": bars[0] = bars[0] with { AvailableAtUtc = dataset.AsOfUtc.AddDays(1) }; break;
            case "incomplete": bars[0] = bars[0] with { CloseTimeUtc = bars[0].OpenTimeUtc }; break;
            case "volume": bars[0] = bars[0] with { Volume = -1 }; break;
            case "spread": bars[0] = bars[0] with { Spread = -1 }; break;
            case "timeframe": bars[0] = bars[0] with { TimeframeMinutes = 1 }; break;
        }
        Assert.Throws<ArgumentException>(() => (dataset with { Bars = bars }).Validate());
    }

    [Fact] public void UnknownFieldsCannotEnterDataset()
    {
        var json = Encoding.UTF8.GetString(ResearchDatasetFile.Serialize(Dataset(6)));
        Assert.Throws<JsonException>(() => ResearchDatasetFile.Load(Encoding.UTF8.GetBytes(json.Insert(1, "\"privateField\":\"test\", "))));
    }

    [Fact] public void FutureBarsDoNotChangeCausalFeatures()
    {
        var dataset = Dataset();
        var time = Epoch.AddMinutes(5 * 610);
        var before = DatasetBacktest.FeaturesAt(dataset, time, Settings().Costs);
        var modified = dataset with { Bars = dataset.Bars.Select(b => b.AvailableAtUtc > time
            ? b with { Open = b.Open + 100, High = b.High + 100, Low = b.Low + 100, Close = b.Close + 100 } : b).ToArray() };
        Assert.NotNull(before);
        Assert.Equal(before, DatasetBacktest.FeaturesAt(modified, time, Settings().Costs));
    }

    [Fact] public void LateContextIsNotUsedEarly()
    {
        var dataset = Dataset(600);
        var time = dataset.AsOfUtc;
        Assert.NotNull(DatasetBacktest.FeaturesAt(dataset, time, Settings().Costs));
        var late = dataset with { Bars = dataset.Bars.Select(b => b.TimeframeMinutes == 15 && b.CloseTimeUtc == time
            ? b with { AvailableAtUtc = time.AddSeconds(1) } : b).ToArray() };
        Assert.Null(DatasetBacktest.FeaturesAt(late, time, Settings().Costs));
    }

    [Fact] public void InsufficientWarmupFailsClosed()
    {
        var dataset = Dataset(60);
        var run = DatasetBacktest.Run(dataset, Settings(), Epoch, dataset.AsOfUtc);
        Assert.Contains("MissingCausalWarmup", run.DataFailures);
        Assert.Empty(run.Trades);
    }

    [Fact] public void BacktestAndBaselineRepeatExactly()
    {
        var data = Dataset();
        foreach (var baseline in new[] { false, true })
        {
            var a = DatasetBacktest.Run(data, Settings(), Epoch.AddHours(50), data.AsOfUtc, baseline);
            var b = DatasetBacktest.Run(data, Settings(), Epoch.AddHours(50), data.AsOfUtc, baseline);
            Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
            Assert.All(a.Trades, t => Assert.True(t.EntryTime >= Epoch.AddHours(50) && t.ExitTime <= data.AsOfUtc));
            if (baseline) Assert.NotEmpty(a.Trades);
        }
    }

    [Fact] public void TransactionCostsReduceProfit()
    {
        var settings = Settings();
        Assert.Equal(94m, settings.Costs.NetProfit(TradeDirection.Buy, 2000, 2001, 1, settings.Symbol));
        Assert.Equal(94m, settings.Costs.NetProfit(TradeDirection.Sell, 2001, 2000, 1, settings.Symbol));
        var data = Dataset();
        var run = DatasetBacktest.Run(data, settings, Epoch.AddHours(50), data.AsOfUtc, true);
        Assert.All(run.Trades, t => Assert.Equal(settings.Costs.NetProfit(t.Direction, t.Entry, t.Exit, t.Volume, settings.Symbol), t.ProfitLoss));
        var first = run.Trades[0];
        var candle = data.Bars.Single(b => b.TimeframeMinutes == 5 && b.OpenTimeUtc == first.EntryTime);
        Assert.Equal(candle.Open + .1m + .02m, first.Entry);
    }

    [Fact] public void MinimumStopsAreEnforced()
    {
        var data = Dataset();
        var settings = Settings() with { Symbol = Settings().Symbol with { MinStopDistance = 100 } };
        Assert.Empty(DatasetBacktest.Run(data, settings, Epoch.AddHours(50), data.AsOfUtc, true).Trades);
    }

    [Fact] public void MetricsIncludeDrawdownAndLosingStreak()
    {
        var result = ResearchMetrics.Calculate(1000, [Trade(100), Trade(-50, 5), Trade(-25, 10)],
            [1000, 1100, 1050, 1025], 50);
        Assert.Equal(1025, result.EndingCapital);
        Assert.Equal(25, result.NetProfit);
        Assert.Equal(75, result.MaximumDrawdown);
        Assert.Equal(75m / 1100 * 100, result.MaximumDrawdownPercent);
        Assert.Equal(100m / 75, result.ProfitFactor);
        Assert.Equal(2, result.MaximumConsecutiveLosses);
        Assert.Equal(1, result.WinningTrades);
        Assert.Equal(2, result.LosingTrades);
        Assert.Equal(-37.5m, result.AverageLoss);
        Assert.Equal(25m / 3, result.Expectancy);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(0, false)]
    public void ZeroLossProfitFactorIsExplicit(decimal pnl, bool unbounded)
    {
        var result = ResearchMetrics.Calculate(1000, [Trade(pnl)], [1000, 1000 + pnl], 0);
        Assert.Null(result.ProfitFactor);
        Assert.Equal(unbounded, result.ProfitFactorUnbounded);
        Assert.Contains("\"ProfitFactor\":null", JsonSerializer.Serialize(result));
    }

    [Fact] public void WalkForwardWindowsAreDisjointAndCausal()
    {
        var plan = WalkForwardValidation.Plan(Dataset(1320), 600, 30, 30);
        Assert.Equal(2, plan.Count);
        Assert.Equal(plan[0].Training.EndUtc, plan[0].Validation.StartUtc);
        Assert.Equal(plan[0].Validation.EndUtc, plan[0].OutOfSample.StartUtc);
        Assert.Equal(plan[0].OutOfSample.EndUtc, plan[1].Training.StartUtc);
        Assert.Equal(Epoch.AddMinutes(630 * 5), plan[0].OutOfSample.StartUtc);
    }

    [Fact] public void OverlapIsMethodFailure()
    {
        var data = Dataset();
        var plan = WalkForwardValidation.Plan(data, 600, 30, 30).ToArray();
        plan[0] = plan[0] with { Validation = plan[0].Validation with { StartUtc = Epoch } };
        var report = WalkForwardValidation.Run(data, Settings(), plan);
        Assert.False(report.Gates.Passed);
        Assert.Contains("OverlappingOrNoncausalWindows", report.Gates.MethodFailures);
    }

    [Fact] public void InsufficientEvidenceFailsEvenWithPositiveResults()
    {
        var data = Dataset();
        var report = WalkForwardValidation.Run(data, Settings(), WalkForwardValidation.Plan(data, 600, 30, 30));
        Assert.False(report.Gates.Passed);
        Assert.Contains("InsufficientEvidence", report.Gates.MethodFailures);
        Assert.Single(report.Folds);
        Assert.NotNull(report.Aggregate);
        Assert.False(report.Robustness!.SufficientSample);
        Assert.Null(report.Robustness.LargestTradeGrossProfitShare);
    }

    [Fact] public void PerformancePolicyIsConfigurable()
    {
        var metrics = ResearchMetrics.Calculate(1000, [Trade(50), Trade(-10)], [1000, 1050, 1040], 10);
        Assert.True(WalkForwardValidation.AcceptPerformance(metrics, new(MinimumTradesPerFold: 2)));
        Assert.False(WalkForwardValidation.AcceptPerformance(metrics, new(MinimumTradesPerFold: 3)));
        Assert.False(WalkForwardValidation.AcceptPerformance(metrics, new(MinimumTradesPerFold: 2, MinimumProfitFactor: 6)));
    }

    [Fact] public void DeclaredClosureAllowsGapButUnexplainedGapFails()
    {
        var data = Dataset(720);
        var closure = new ResearchClosure(Epoch.AddHours(51), Epoch.AddHours(52));
        data = data with { Bars = data.Bars.Where(b => b.CloseTimeUtc <= closure.StartUtc ||
            b.OpenTimeUtc >= closure.EndUtc).ToArray() };
        var without = DatasetBacktest.Run(data, Settings(), Epoch.AddHours(50), data.AsOfUtc, true);
        Assert.Contains("CalendarGapRequiresReview", without.DataFailures);
        var withCalendar = DatasetBacktest.Run(data, Settings() with { ScheduledClosures = [closure] },
            Epoch.AddHours(50), data.AsOfUtc, true);
        Assert.Empty(withCalendar.DataFailures);
        Assert.All(withCalendar.Trades, t => Assert.False(t.EntryTime < closure.StartUtc && t.ExitTime > closure.StartUtc));
    }

    [Fact] public void CandlesInsideDeclaredClosureFailDataQuality()
    {
        var data = Dataset(660);
        var run = DatasetBacktest.Run(data, Settings() with { ScheduledClosures =
            [new(Epoch.AddHours(51), Epoch.AddHours(52))] }, Epoch.AddHours(50), data.AsOfUtc);
        Assert.Contains("BarsInsideScheduledClosure", run.DataFailures);
    }

    [Fact] public void TrendPullbackDatasetRunProducesRepeatableTrades()
    {
        var data = Dataset(780);
        var settings = Settings() with { Strategy = new TrendPullbackOptions(
            BuyRsiMinimum: 0, BuyRsiMaximum: 100, PullbackAtrTolerance: 10,
            MaximumSpreadAtrRatio: 1) };
        var start = Epoch.AddHours(55);
        var first = DatasetBacktest.Run(data, settings, start, data.AsOfUtc);
        var repeated = DatasetBacktest.Run(data, settings, start, data.AsOfUtc);
        Assert.NotEmpty(first.Trades);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(repeated));
        Assert.Empty(first.DataFailures);
    }

    [Fact] public void ValidSyntheticEvidencePassesDataIntegrityButFailsMethodology()
    {
        var data = Dataset() with { Source = ResearchSource.SyntheticFixture };
        var report = WalkForwardValidation.Run(data, Settings(), WalkForwardValidation.Plan(data, 600, 30, 30));
        Assert.True(report.Gates.DataIntegrity);
        Assert.Empty(report.Gates.DataFailures);
        Assert.Contains("SyntheticEvidenceOnly", report.Gates.MethodFailures);
        Assert.False(report.Gates.LeakageAndReproducibility);
        Assert.False(report.Gates.Passed);
    }

    [Fact] public void InvalidContractAndCostsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => (Settings() with
            { Costs = Settings().Costs with { SlippagePricePerSide = -1 } }).Validate());
        Assert.Throws<ArgumentException>(() => (Settings() with
            { Symbol = Settings().Symbol with { ContractSize = 10 } }).Validate());
    }

    [Fact] public void EmaUsesFullCausalHistoryAndMatchesExistingIndicator()
    {
        var dataset = Dataset(900);
        var features = DatasetBacktest.FeaturesAt(dataset, dataset.AsOfUtc, Settings().Costs)!;
        var prices = dataset.Bars.Where(b => b.TimeframeMinutes == 15).Select(b => b.Close).ToArray();
        Assert.Equal(GoldAiTrader.Market.Indicators.Ema(prices, 200), features.ContextSlowEma);
        Assert.NotEqual(prices.TakeLast(200).Average(), features.ContextSlowEma);
    }

    [Fact] public void MissingOrDuplicateJsonFieldsAreRejected()
    {
        var json = Encoding.UTF8.GetString(ResearchDatasetFile.Serialize(Dataset(6)));
        Assert.Throws<JsonException>(() => ResearchDatasetFile.Load(Encoding.UTF8.GetBytes(
            json.Replace("\"FormatVersion\":1,", ""))));
        Assert.Throws<JsonException>(() => ResearchDatasetFile.Load(Encoding.UTF8.GetBytes(
            json.Insert(1, "\"FormatVersion\":1,"))));
    }

    [Fact] public void EmptyWindowIsCategorizedAsDataFailure()
    {
        var data = Dataset();
        var end = Epoch.AddDays(-1);
        var folds = new[] { new WalkForwardFold(new(end.AddDays(-3), end.AddDays(-2)),
            new(end.AddDays(-2), end.AddDays(-1)), new(end.AddDays(-1), end)) };
        var report = WalkForwardValidation.Run(data, Settings(), folds);
        Assert.Contains("WindowCoverage", report.Gates.DataFailures);
        Assert.False(report.Gates.Passed);
    }

    [Fact] public void NewFoldCannotBorrowWarmupFromEarlierFold()
    {
        var data = Dataset(1320);
        var plan = WalkForwardValidation.Plan(data, 600, 30, 30).ToArray();
        plan[1] = plan[1] with { Training = plan[1].Training with
            { StartUtc = plan[1].Training.EndUtc.AddMinutes(-5) } };
        var report = WalkForwardValidation.Run(data, Settings(), plan);
        Assert.Contains("MissingCausalWarmup", report.Folds[1].Validation.DataFailures);
        Assert.False(report.Gates.DataIntegrity);
    }

    [Fact] public void FinalTestPricesCannotChangeValidationTradesOrSensitivity()
    {
        var data = Dataset();
        var plan = WalkForwardValidation.Plan(data, 600, 30, 30);
        var testStart = plan[0].OutOfSample.StartUtc;
        var changed = data with { Bars = data.Bars.Select(b => b.OpenTimeUtc >= testStart
            ? b with { Open = b.Open + 10, High = b.High + 10, Low = b.Low + 10, Close = b.Close + 10 } : b).ToArray() };
        var before = WalkForwardValidation.Run(data, Settings(), plan).Folds[0];
        var after = WalkForwardValidation.Run(changed, Settings(), plan).Folds[0];
        Assert.Equal(before.Validation.Metrics, after.Validation.Metrics);
        Assert.Equal(before.HigherCosts.Metrics, after.HigherCosts.Metrics);
        Assert.Equal(before.ParameterSensitivity.Metrics, after.ParameterSensitivity.Metrics);
        Assert.Equal(JsonSerializer.Serialize(before.Validation.Trades), JsonSerializer.Serialize(after.Validation.Trades));
    }

    [Fact] public void SameBarStopAndTargetChooseStopWithNetRiskBudget()
    {
        var data = Dataset(601);
        var entryTime = Epoch.AddHours(50);
        data = data with { Bars = data.Bars.Select(b => b.TimeframeMinutes == 5 && b.OpenTimeUtc == entryTime
            ? b with { High = b.Open + 20, Low = b.Open - 20 } : b).ToArray() };
        var run = DatasetBacktest.Run(data, Settings(), entryTime, data.AsOfUtc, true);
        var trade = Assert.Single(run.Trades);
        Assert.Equal("Stop", trade.ExitReason);
        Assert.InRange(-trade.ProfitLoss, 0, 50);
        Assert.Equal(-1m, trade.RMultiple);
        Assert.Equal(100, run.Metrics.ExposurePercent);
    }

    [Fact] public void AdverseGapIsNotFilledAtOldStop()
    {
        var data = Dataset(602);
        var entryTime = Epoch.AddHours(50);
        data = data with { Bars = data.Bars.Select(b => b.TimeframeMinutes == 5 && b.OpenTimeUtc > entryTime
            ? b with { Open = b.Open - 100, High = b.High - 100, Low = b.Low - 100, Close = b.Close - 100 } : b).ToArray() };
        var run = DatasetBacktest.Run(data, Settings(), entryTime, data.AsOfUtc, true);
        Assert.True(Assert.Single(run.Trades).ProfitLoss < -50);
        Assert.Contains("DailyLoss", run.RiskFailures);
        Assert.Contains("Drawdown", run.RiskFailures);
    }

    [Fact] public void CaptureIsIdempotentPrivateAndConflictSafe()
    {
        var directory = Path.Combine(Path.GetTempPath(), "research-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new ResearchBarArchive(directory);
            var dataset = Dataset(1) with { Source = ResearchSource.Mt5Telemetry };
            archive.Capture(dataset);
            archive.Capture(dataset with { AsOfUtc = dataset.AsOfUtc.AddSeconds(1),
                Bars = [dataset.Bars[0] with { AvailableAtUtc = dataset.AsOfUtc.AddSeconds(1) }] });
            Assert.Single(Directory.GetFiles(directory));
            var exported = archive.Export(dataset.AsOfUtc);
            var text = Encoding.UTF8.GetString(ResearchDatasetFile.Serialize(exported));
            Assert.DoesNotContain("Account", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BridgeInstance", text, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<InvalidOperationException>(() => archive.Capture(dataset with
                { Bars = [dataset.Bars[0] with { Close = dataset.Bars[0].Close + .01m }] }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
