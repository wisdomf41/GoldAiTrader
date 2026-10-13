using GoldAiTrader.Core;
using GoldAiTrader.Data;
using GoldAiTrader.Market;
using GoldAiTrader.Risk;
using GoldAiTrader.Strategy;

namespace GoldAiTrader.Backtesting;

public sealed record ResearchCosts(decimal FallbackSpread, decimal CommissionPerLotPerSide,
    decimal SlippagePricePerSide, decimal SpreadMultiplier = 1m)
{
    public void Validate()
    {
        if (FallbackSpread < 0 || CommissionPerLotPerSide < 0 || SlippagePricePerSide < 0 || SpreadMultiplier < 1)
            throw new ArgumentException("Invalid research costs.");
    }

    public decimal NetProfit(TradeDirection direction, decimal entry, decimal exit, decimal volume,
        SymbolSpecification symbol) =>
        (direction == TradeDirection.Buy ? exit - entry : entry - exit) / symbol.TickSize *
        symbol.TickValuePerVolumeUnit * volume - 2 * CommissionPerLotPerSide * volume;
}

public sealed record ResearchClosure(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public sealed record ResearchRunSettings(decimal InitialCapital, SymbolSpecification Symbol,
    ResearchCosts Costs, TrendPullbackOptions Strategy, RiskOptions Risk,
    IReadOnlyList<ResearchClosure>? ScheduledClosures = null)
{
    public void Validate()
    {
        Costs.Validate();
        DateTimeOffset? priorEnd = null;
        foreach (var closure in ScheduledClosures ?? [])
        {
            if (!ResearchDataset.Utc(closure.StartUtc) || !ResearchDataset.Utc(closure.EndUtc) ||
                closure.StartUtc >= closure.EndUtc || priorEnd > closure.StartUtc ||
                closure.StartUtc.Ticks % TimeSpan.FromMinutes(15).Ticks != 0 ||
                closure.EndUtc.Ticks % TimeSpan.FromMinutes(15).Ticks != 0)
                throw new ArgumentException("Invalid or overlapping scheduled closure.");
            priorEnd = closure.EndUtc;
        }
        Risk.Validate();
        if (InitialCapital <= 0 || !Symbol.IsValid || Symbol.CanonicalSymbol != "XAUUSD" ||
            Symbol.TickSize * Symbol.ContractSize != Symbol.TickValuePerVolumeUnit ||
            Strategy.StopAtrMultiple <= 0 || Strategy.RewardRisk <= 0 || Strategy.PullbackAtrTolerance < 0 ||
            Strategy.MinimumAdx is < 0 or > 100 || Strategy.BuyRsiMinimum < 0 || Strategy.BuyRsiMaximum > 100 ||
            Strategy.BuyRsiMinimum > Strategy.BuyRsiMaximum || Strategy.SellRsiMinimum < 0 ||
            Strategy.SellRsiMaximum > 100 || Strategy.SellRsiMinimum > Strategy.SellRsiMaximum ||
            Strategy.MaximumSpread < 0 || Strategy.MaximumSpreadAtrRatio < 0 || Strategy.MaximumVolatility < 0 ||
            Strategy.MaximumDailyTrades < 1 || Strategy.EffectiveCooldown < TimeSpan.Zero ||
            Strategy.EffectiveMaximumHoldingPeriod <= TimeSpan.Zero)
            throw new ArgumentException("Invalid research settings; version 1 requires USD-quoted linear contracts.");
    }
}

public sealed record ResearchTrade(DateTimeOffset EntryTime, DateTimeOffset ExitTime,
    TradeDirection Direction, decimal Entry, decimal Exit, decimal Volume, decimal ProfitLoss,
    decimal RMultiple, string ExitReason);

public sealed record ResearchMetrics(decimal StartingCapital, decimal EndingCapital, decimal NetProfit,
    decimal ReturnPercent, decimal MaximumDrawdown, decimal MaximumDrawdownPercent,
    int TradeCount, int WinningTrades, int LosingTrades, decimal WinRatePercent,
    decimal AverageWin, decimal AverageLoss, decimal Expectancy, decimal GrossProfit, decimal GrossLoss,
    decimal? ProfitFactor, bool ProfitFactorUnbounded, decimal AverageR, int MaximumConsecutiveLosses,
    decimal ExposurePercent)
{
    public static ResearchMetrics Calculate(decimal capital, IReadOnlyList<ResearchTrade> trades,
        IReadOnlyList<decimal> equity, decimal exposurePercent)
    {
        if (capital <= 0 || equity.Count == 0 || exposurePercent is < 0 or > 100)
            throw new ArgumentException("Invalid metric inputs.");
        var wins = trades.Where(t => t.ProfitLoss > 0).Select(t => t.ProfitLoss).ToArray();
        var losses = trades.Where(t => t.ProfitLoss < 0).Select(t => t.ProfitLoss).ToArray();
        var grossProfit = wins.Sum();
        var grossLoss = -losses.Sum();
        var net = grossProfit - grossLoss;
        decimal peak = capital, drawdown = 0, drawdownPercent = 0;
        foreach (var value in equity)
        {
            peak = Math.Max(peak, value);
            drawdown = Math.Max(drawdown, peak - value);
            drawdownPercent = Math.Max(drawdownPercent, (peak - value) / peak * 100);
        }
        int streak = 0, maxStreak = 0;
        foreach (var trade in trades)
        {
            streak = trade.ProfitLoss < 0 ? streak + 1 : 0;
            maxStreak = Math.Max(maxStreak, streak);
        }
        return new(capital, capital + net, net, net / capital * 100, drawdown, drawdownPercent,
            trades.Count, wins.Length, losses.Length, trades.Count == 0 ? 0 : 100m * wins.Length / trades.Count,
            wins.Length == 0 ? 0 : wins.Average(), losses.Length == 0 ? 0 : losses.Average(),
            trades.Count == 0 ? 0 : net / trades.Count, grossProfit, grossLoss,
            grossLoss > 0 ? grossProfit / grossLoss : null, grossLoss == 0 && grossProfit > 0,
            trades.Count == 0 ? 0 : trades.Average(t => t.RMultiple), maxStreak, exposurePercent);
    }
}

public sealed record ResearchRun(string DatasetHash, string EngineVersion, string ImplementationFingerprint, string StrategyVersion,
    ResearchRunSettings Settings, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    bool ReferenceBaseline, int ObservedBars, IReadOnlyList<ResearchTrade> Trades, IReadOnlyList<decimal> Equity,
    ResearchMetrics Metrics, IReadOnlyList<string> DataFailures, IReadOnlyList<string> RiskFailures);

public static class DatasetBacktest
{
    public const string EngineVersion = "dataset-research-v1";
    private static readonly string ImplementationFingerprint = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join("|", new[] { typeof(DatasetBacktest), typeof(ResearchDatasetFile),
                typeof(ResearchDataset), typeof(Indicators), typeof(TrendPullbackV1), typeof(RiskEngine) }
                .Select(type => type.Module.ModuleVersionId.ToString("D"))))));

    public static MarketFeatures? FeaturesAt(ResearchDataset dataset, DateTimeOffset decisionAt,
        ResearchCosts costs) => FeaturesAt(new FeatureHistory(dataset, 5, 20, 50),
            new FeatureHistory(dataset, 15, 50, 200), decisionAt, costs);

    private sealed class FeatureHistory
    {
        private readonly ResearchBar[] stream;
        private readonly decimal[] fast;
        private readonly decimal[] slow;

        public FeatureHistory(ResearchDataset dataset, int timeframe, int fastPeriod, int slowPeriod)
        {
            stream = dataset.Bars.Where(b => b.TimeframeMinutes == timeframe).ToArray();
            fast = Emas(fastPeriod);
            slow = Emas(slowPeriod);
        }

        private decimal[] Emas(int period)
        {
            var result = new decimal[stream.Length];
            decimal sum = 0;
            for (var i = 0; i < stream.Length; i++)
            {
                if (i < period) sum += stream[i].Close;
                if (i == period - 1) result[i] = sum / period;
                else if (i >= period)
                {
                    var multiplier = 2m / (period + 1m);
                    result[i] = stream[i].Close * multiplier + result[i - 1] * (1 - multiplier);
                }
            }
            return result;
        }

        public (ResearchBar[] Bars, decimal Fast, decimal Slow) At(DateTimeOffset time)
        {
            int left = 0, right = stream.Length;
            while (left < right)
            {
                var middle = left + (right - left) / 2;
                if (stream[middle].AvailableAtUtc <= time) left = middle + 1;
                else right = middle;
            }
            return (stream[Math.Max(0, left - 200)..left],
                left == 0 ? 0 : fast[left - 1], left == 0 ? 0 : slow[left - 1]);
        }
    }

    private static MarketFeatures? FeaturesAt(FeatureHistory executionHistory, FeatureHistory contextHistory,
        DateTimeOffset decisionAt, ResearchCosts costs)
    {
        var (execution, executionFast, executionSlow) = executionHistory.At(decisionAt);
        var (context, contextFast, contextSlow) = contextHistory.At(decisionAt);
        if (execution.Length < 50 || context.Length < 200 ||
            decisionAt - execution[^1].CloseTimeUtc > TimeSpan.FromMinutes(10) ||
            decisionAt - context[^1].CloseTimeUtc > TimeSpan.FromMinutes(30)) return null;
        var prices = execution.Select(b => b.Close).ToArray();
        var bars = execution.Select(b => b.ToMarketBar()).ToArray();
        var atr = Indicators.Atr(bars, 14);
        var adx = Indicators.Adx(context.Select(b => b.ToMarketBar()).ToArray(), 14);
        var spread = (execution[^1].Spread ?? costs.FallbackSpread) * costs.SpreadMultiplier;
        var bid = execution[^1].Close;
        return new(decisionAt, "XAUUSD", bid, bid + spread, executionFast,
            executionSlow, contextFast, contextSlow,
            atr, Indicators.Rsi(prices, 14), adx, atr / bid, SessionClassifier.Classify(decisionAt),
            adx >= 20 ? MarketRegime.Trending : MarketRegime.Ranging);
    }

    public static ResearchRun Run(ResearchDataset dataset, ResearchRunSettings settings,
        DateTimeOffset startUtc, DateTimeOffset endUtc, bool referenceBaseline = false)
    {
        dataset.Validate();
        settings.Validate();
        if (!ResearchDataset.Utc(startUtc) || !ResearchDataset.Utc(endUtc) || startUtc >= endUtc ||
            endUtc > dataset.AsOfUtc || settings.Symbol.BrokerSymbol != dataset.BrokerSymbol)
            throw new ArgumentException("Invalid research window or broker contract.");
        var bars = dataset.Bars.Where(b => b.TimeframeMinutes == 5 &&
            b.OpenTimeUtc >= startUtc && b.CloseTimeUtc <= endUtc).ToArray();
        var dataFailures = new HashSet<string>(StringComparer.Ordinal);
        var riskFailures = new HashSet<string>(StringComparer.Ordinal);
        var closures = settings.ScheduledClosures ?? [];
        if (bars.Length == 0 ||
            (bars[0].OpenTimeUtc != startUtc && !closures.Any(c => c.StartUtc == startUtc && c.EndUtc == bars[0].OpenTimeUtc)) ||
            (bars[^1].CloseTimeUtc != endUtc && !closures.Any(c => c.StartUtc == bars[^1].CloseTimeUtc && c.EndUtc == endUtc)))
            dataFailures.Add("WindowCoverage");
        if (dataset.Bars.Any(b => closures.Any(c => b.OpenTimeUtc < c.EndUtc && b.CloseTimeUtc > c.StartUtc)))
            dataFailures.Add("BarsInsideScheduledClosure");
        // A trading calendar is deliberately not inferred from observed prices.
        foreach (var stream in dataset.Bars.Where(b => b.CloseTimeUtc <= endUtc &&
                     b.OpenTimeUtc >= startUtc.AddHours(-50)).GroupBy(b => b.TimeframeMinutes))
        {
            var previous = stream.First();
            foreach (var bar in stream.Skip(1))
            {
                if (bar.OpenTimeUtc != previous.CloseTimeUtc && !closures.Any(c =>
                        c.StartUtc == previous.CloseTimeUtc && c.EndUtc == bar.OpenTimeUtc))
                    dataFailures.Add("CalendarGapRequiresReview");
                previous = bar;
            }
        }

        var executionStream = new FeatureHistory(dataset, 5, 20, 50);
        var contextStream = new FeatureHistory(dataset, 15, 50, 200);
        var strategy = new TrendPullbackV1(settings.Strategy);
        var risk = new RiskEngine(settings.Risk);
        var state = new RiskState();
        var trades = new List<ResearchTrade>();
        var equity = new List<decimal> { settings.InitialCapital };
        decimal balance = settings.InitialCapital, peak = balance, dayStart = balance, weekStart = balance;
        DateOnly? day = null;
        DateOnly? week = null;
        DateTimeOffset? lastExit = null;
        Position? position = null;
        var tradesToday = 0;
        var exposedBars = 0;
        foreach (var bar in bars)
        {
            var thisDay = DateOnly.FromDateTime(bar.OpenTimeUtc.UtcDateTime);
            var thisWeek = thisDay.AddDays(-((int)thisDay.DayOfWeek + 6) % 7);
            if (day != thisDay) { day = thisDay; dayStart = equity[^1]; tradesToday = 0; }
            if (week != thisWeek) { week = thisWeek; weekStart = equity[^1]; }
            var features = FeaturesAt(executionStream, contextStream, bar.OpenTimeUtc, settings.Costs);
            if (features is null)
            {
                var reopening = closures.Any(c => bar.OpenTimeUtc >= c.EndUtc &&
                    bar.OpenTimeUtc < c.EndUtc.AddMinutes(30));
                var warmed = contextStream.At(bar.OpenTimeUtc).Bars.Length >= 200 &&
                    executionStream.At(bar.OpenTimeUtc).Bars.Length >= 50;
                if (!reopening || !warmed) dataFailures.Add("MissingCausalWarmup");
            }
            var spread = features is null ? settings.Costs.FallbackSpread * settings.Costs.SpreadMultiplier
                : features.Ask - features.Bid;
            if (position is null && features is not null)
            {
                TradeSetup? setup;
                if (referenceBaseline)
                {
                    var distance = features.Atr * 1.5m;
                    setup = tradesToday < settings.Strategy.MaximumDailyTrades &&
                        (lastExit is null || bar.OpenTimeUtc - lastExit >= settings.Strategy.EffectiveCooldown)
                        ? new(Guid.Empty, bar.OpenTimeUtc, "XAUUSD", TradeDirection.Buy,
                            features.Ask, features.Ask - distance, features.Ask + 2 * distance,
                            "LongAtrReferenceV1", "Fixed long reference.") : null;
                }
                else setup = strategy.Evaluate(features, new(tradesToday, false, lastExit)).Setup;
                if (setup is not null)
                {
                    var sign = setup.Direction == TradeDirection.Buy ? 1m : -1m;
                    var entry = bar.Open + (sign > 0 ? spread : 0) + sign * settings.Costs.SlippagePricePerSide;
                    setup = setup with { SignalId = Guid.Empty, EntryPrice = entry,
                        StopLoss = entry - sign * setup.StopDistance,
                        TakeProfit = entry + sign * setup.RewardDistance };
                    var assessment = risk.Assess(setup, new(balance, equity[^1], dayStart, weekStart,
                        peak, peak, 0, 0), settings.Symbol, spread, state, bar.OpenTimeUtc);
                    if (assessment.Approved)
                    {
                        var perLotRisk = (setup.StopDistance + settings.Costs.SlippagePricePerSide) /
                            settings.Symbol.TickSize * settings.Symbol.TickValuePerVolumeUnit +
                            2 * settings.Costs.CommissionPerLotPerSide;
                        var budget = equity[^1] * settings.Risk.RiskPerTradePercent / 100;
                        var volume = Math.Floor(Math.Min(assessment.PositionSize!.Volume, budget / perLotRisk) /
                            settings.Symbol.VolumeStep) * settings.Symbol.VolumeStep;
                        if (volume >= settings.Symbol.MinVolume)
                        {
                            position = new(setup, volume, perLotRisk * volume);
                            tradesToday++;
                        }
                    }
                }
            }
            if (position is not null)
            {
                exposedBars++;
                var p = position;
                var buy = p.Setup.Direction == TradeDirection.Buy;
                var high = bar.High + (buy ? 0 : spread);
                var low = bar.Low + (buy ? 0 : spread);
                var open = bar.Open + (buy ? 0 : spread);
                var stopHit = buy ? low <= p.Setup.StopLoss : high >= p.Setup.StopLoss;
                var targetHit = buy ? high >= p.Setup.TakeProfit : low <= p.Setup.TakeProfit;
                var timeout = bar.CloseTimeUtc - p.Setup.Time >= settings.Strategy.EffectiveMaximumHoldingPeriod;
                var final = bar == bars[^1];
                var sessionClose = closures.Any(c => c.StartUtc == bar.CloseTimeUtc);
                var exit = stopHit ? (buy ? Math.Min(open, p.Setup.StopLoss) : Math.Max(open, p.Setup.StopLoss))
                    : targetHit ? p.Setup.TakeProfit : bar.Close + (buy ? 0 : spread);
                exit += buy ? -settings.Costs.SlippagePricePerSide : settings.Costs.SlippagePricePerSide;
                // A conservative intrabar equity lower bound, capped at an executed stop.
                var adverse = stopHit ? exit : (buy ? low : high) +
                    (buy ? -settings.Costs.SlippagePricePerSide : settings.Costs.SlippagePricePerSide);
                var worstEquity = balance + settings.Costs.NetProfit(p.Setup.Direction,
                    p.Setup.EntryPrice, adverse, p.Volume, settings.Symbol);
                equity.Add(worstEquity);
                CheckRisk(worstEquity);
                if (stopHit || targetHit || timeout || final || sessionClose)
                {
                    var pnl = settings.Costs.NetProfit(p.Setup.Direction, p.Setup.EntryPrice, exit, p.Volume, settings.Symbol);
                    trades.Add(new(p.Setup.Time, bar.CloseTimeUtc, p.Setup.Direction, p.Setup.EntryPrice,
                        exit, p.Volume, pnl, pnl / p.Risk, stopHit ? "Stop" : targetHit ? "Target" : timeout ? "Timeout" : sessionClose ? "SessionClose" : "WindowEnd"));
                    balance += pnl;
                    state = RiskStateTracker.RecordClosedTrade(state, pnl, bar.CloseTimeUtc, settings.Risk);
                    lastExit = bar.CloseTimeUtc;
                    position = null;
                    equity.Add(balance);
                }
                else equity.Add(balance + settings.Costs.NetProfit(p.Setup.Direction, p.Setup.EntryPrice,
                    bar.Close + (buy ? 0 : spread), p.Volume, settings.Symbol));
            }
            else equity.Add(balance);
            peak = Math.Max(peak, Math.Max(balance, equity[^1]));
            CheckRisk(equity[^1]);
        }
        var exposure = bars.Length == 0 ? 0 : 100m * exposedBars / bars.Length;
        return new(ResearchDatasetFile.Fingerprint(dataset), EngineVersion, ImplementationFingerprint, referenceBaseline ? "LongAtrReferenceV1" : TrendPullbackV1.Version,
            settings, startUtc, endUtc, referenceBaseline, bars.Length, trades, equity,
            ResearchMetrics.Calculate(settings.InitialCapital, trades, equity, exposure),
            dataFailures.Order().ToArray(), riskFailures.Order().ToArray());

        void CheckRisk(decimal value)
        {
            if (RiskEngine.LossPercent(dayStart, value) >= settings.Risk.DailyHardStopPercent) riskFailures.Add("DailyLoss");
            if (RiskEngine.LossPercent(weekStart, value) >= settings.Risk.WeeklyHardStopPercent) riskFailures.Add("WeeklyLoss");
            if (RiskEngine.LossPercent(peak, value) >= settings.Risk.HardDrawdownPercent) riskFailures.Add("Drawdown");
        }
    }

    private sealed record Position(TradeSetup Setup, decimal Volume, decimal Risk);
}
