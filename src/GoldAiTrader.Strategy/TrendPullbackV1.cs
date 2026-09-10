using GoldAiTrader.Core;

namespace GoldAiTrader.Strategy;

public sealed record TrendPullbackOptions(decimal MinimumAdx = 20m, decimal BuyRsiMinimum = 50m,
    decimal BuyRsiMaximum = 70m, decimal SellRsiMinimum = 30m, decimal SellRsiMaximum = 50m,
    decimal PullbackAtrTolerance = 0.35m, decimal StopAtrMultiple = 1.5m, decimal RewardRisk = 2m,
    decimal MaximumSpread = 0.50m, decimal MaximumSpreadAtrRatio = 0.10m,
    decimal MaximumVolatility = 0.02m, int MaximumDailyTrades = 3,
    TimeSpan? Cooldown = null, TimeSpan? MaximumHoldingPeriod = null)
{
    public TimeSpan EffectiveCooldown => Cooldown ?? TimeSpan.FromMinutes(30);
    public TimeSpan EffectiveMaximumHoldingPeriod => MaximumHoldingPeriod ?? TimeSpan.FromHours(4);
}

public sealed record StrategyContext(int TradesToday, bool HasOpenPosition, DateTimeOffset? LastExitTime);

public sealed class TrendPullbackV1(TrendPullbackOptions options)
{
    public const string Version = "TrendPullbackV1";

    public StrategyDecision Evaluate(MarketFeatures features, StrategyContext context)
    {
        if (!features.IsValid) return StrategyDecision.Wait("Invalid market data.");
        if (!string.Equals(features.Symbol, "XAUUSD", StringComparison.Ordinal)) return StrategyDecision.Wait("TrendPullbackV1 requires canonical XAUUSD.");
        if (context.HasOpenPosition) return StrategyDecision.Wait("Maximum one position.");
        if (context.TradesToday >= options.MaximumDailyTrades) return StrategyDecision.Wait("Daily trade limit reached.");
        if (context.LastExitTime is { } last && features.Time - last < options.EffectiveCooldown)
            return StrategyDecision.Wait("Cooldown active.");
        if (features.Session == MarketSession.Closed) return StrategyDecision.Wait("Outside configured sessions.");
        if (features.Regime != MarketRegime.Trending || features.Adx < options.MinimumAdx)
            return StrategyDecision.Wait("Trend strength or regime filter.");
        if (features.Spread > options.MaximumSpread || features.Spread / features.Atr > options.MaximumSpreadAtrRatio ||
            features.Volatility > options.MaximumVolatility)
            return StrategyDecision.Wait("Market cost or volatility filter.");

        var tolerance = features.Atr * options.PullbackAtrTolerance;
        var buy = features.ContextFastEma > features.ContextSlowEma && features.FastEma > features.SlowEma &&
            Math.Abs(features.Bid - features.FastEma) <= tolerance &&
            features.Rsi >= options.BuyRsiMinimum && features.Rsi <= options.BuyRsiMaximum;
        var sell = features.ContextFastEma < features.ContextSlowEma && features.FastEma < features.SlowEma &&
            Math.Abs(features.Ask - features.FastEma) <= tolerance &&
            features.Rsi >= options.SellRsiMinimum && features.Rsi <= options.SellRsiMaximum;
        if (!buy && !sell) return StrategyDecision.Wait("Entry confirmation absent.");

        var direction = buy ? TradeDirection.Buy : TradeDirection.Sell;
        var entry = buy ? features.Ask : features.Bid;
        var stopDistance = features.Atr * options.StopAtrMultiple;
        var stop = buy ? entry - stopDistance : entry + stopDistance;
        var target = buy ? entry + stopDistance * options.RewardRisk : entry - stopDistance * options.RewardRisk;
        var setup = new TradeSetup(Guid.NewGuid(), features.Time, features.Symbol, direction, entry, stop, target,
            Version, "EMA trend and pullback with ADX, RSI and ATR confirmation.");
        return new(buy ? StrategyAction.Buy : StrategyAction.Sell, setup.Reason, setup);
    }
}
