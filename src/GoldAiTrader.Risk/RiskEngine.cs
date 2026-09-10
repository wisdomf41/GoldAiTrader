using GoldAiTrader.Core;

namespace GoldAiTrader.Risk;

public sealed record RiskOptions(decimal RiskPerTradePercent = 0.50m, decimal DailyWarningPercent = 1.50m,
    decimal DailyHardStopPercent = 2.00m, decimal WeeklyHardStopPercent = 4.00m,
    decimal DrawdownWarningPercent = 8.00m, decimal HardDrawdownPercent = 12.00m,
    decimal MaxTotalRiskPercent = 0.50m, int MaxOpenPositions = 1, decimal MaxSpread = 0.50m,
    int ConsecutiveLossLimit = 3, TimeSpan? ConsecutiveLossCooldown = null)
{
    public TimeSpan EffectiveConsecutiveLossCooldown => ConsecutiveLossCooldown ?? TimeSpan.FromHours(4);

    public void Validate()
    {
        if (RiskPerTradePercent <= 0 || RiskPerTradePercent > 100) throw new ArgumentOutOfRangeException(nameof(RiskPerTradePercent));
        if (MaxTotalRiskPercent <= 0 || MaxTotalRiskPercent > 100) throw new ArgumentOutOfRangeException(nameof(MaxTotalRiskPercent));
        if (RiskPerTradePercent > MaxTotalRiskPercent) throw new ArgumentException("Per-trade risk cannot exceed total risk.");
        if (MaxOpenPositions <= 0) throw new ArgumentOutOfRangeException(nameof(MaxOpenPositions));
        if (MaxSpread < 0) throw new ArgumentOutOfRangeException(nameof(MaxSpread));
        if (DailyWarningPercent <= 0 || DailyWarningPercent >= DailyHardStopPercent) throw new ArgumentOutOfRangeException(nameof(DailyWarningPercent));
        if (DailyHardStopPercent <= 0 || DailyHardStopPercent > 100) throw new ArgumentOutOfRangeException(nameof(DailyHardStopPercent));
        if (WeeklyHardStopPercent <= 0 || WeeklyHardStopPercent > 100) throw new ArgumentOutOfRangeException(nameof(WeeklyHardStopPercent));
        if (DrawdownWarningPercent <= 0 || DrawdownWarningPercent >= HardDrawdownPercent) throw new ArgumentOutOfRangeException(nameof(DrawdownWarningPercent));
        if (HardDrawdownPercent <= 0 || HardDrawdownPercent > 100) throw new ArgumentOutOfRangeException(nameof(HardDrawdownPercent));
        if (ConsecutiveLossLimit <= 0) throw new ArgumentOutOfRangeException(nameof(ConsecutiveLossLimit));
        if (EffectiveConsecutiveLossCooldown <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ConsecutiveLossCooldown));
    }
}

public sealed record RiskState(int ConsecutiveLosses = 0, DateTimeOffset? SuspendedUntil = null);

public static class RiskStateTracker
{
    public static RiskState RecordClosedTrade(RiskState state, decimal profitLoss, DateTimeOffset closedAt, RiskOptions options)
    {
        options.Validate();
        if (profitLoss >= 0) return new();
        var losses = state.ConsecutiveLosses + 1;
        return losses >= options.ConsecutiveLossLimit
            ? new(losses, closedAt + options.EffectiveConsecutiveLossCooldown)
            : new(losses);
    }
}

public sealed class RiskEngine
{
    private readonly RiskOptions options;

    public RiskEngine(RiskOptions options)
    {
        options.Validate();
        this.options = options;
    }

    public RiskAssessment Assess(TradeSetup setup, AccountSnapshot account, SymbolSpecification symbol, decimal spread,
        RiskState? state = null, DateTimeOffset? evaluationTime = null)
    {
        if (!Valid(account, symbol, setup, spread)) return Reject("Invalid or unsafe input.");
        if (account.EmergencyShutdown) return Reject("Emergency shutdown active.");
        if (account.OpenPositions >= options.MaxOpenPositions) return Reject("Maximum open positions reached.");
        if (state is { SuspendedUntil: { } until } && (evaluationTime ?? setup.Time) < until)
            return Reject("Consecutive-loss cooldown active.");
        if (spread > options.MaxSpread) return Reject("Spread exceeds limit.");
        if (LossPercent(account.DayStartEquity, account.Equity) >= options.DailyHardStopPercent) return Reject("Daily hard loss limit reached.");
        if (LossPercent(account.WeekStartEquity, account.Equity) >= options.WeeklyHardStopPercent) return Reject("Weekly hard loss limit reached.");
        var drawdown = Math.Max(LossPercent(account.PeakEquity, account.Equity), LossPercent(account.PeakBalance, account.Balance));
        if (drawdown >= options.HardDrawdownPercent) return Reject("Hard drawdown shutdown.");

        var riskAmount = account.Equity * options.RiskPerTradePercent / 100m;
        var ticks = setup.StopDistance / symbol.TickSize;
        var rawVolume = riskAmount / (ticks * symbol.TickValuePerVolumeUnit);
        var volume = Math.Floor(rawVolume / symbol.VolumeStep) * symbol.VolumeStep;
        if (volume < symbol.MinVolume) return Reject("Broker minimum volume would exceed risk.");
        volume = Math.Min(volume, symbol.MaxVolume);
        var plannedRisk = volume * ticks * symbol.TickValuePerVolumeUnit;
        var totalRiskLimit = account.Equity * options.MaxTotalRiskPercent / 100m;
        if (plannedRisk > riskAmount || account.OpenRiskAmount + plannedRisk > totalRiskLimit)
            return Reject("Total exposure limit exceeded.");
        var warning = LossPercent(account.DayStartEquity, account.Equity) >= options.DailyWarningPercent ||
            drawdown >= options.DrawdownWarningPercent;
        return new(true, warning ? "Approved in defensive warning state." : "Approved.", new(volume, plannedRisk), warning);
    }

    public static decimal LossPercent(decimal reference, decimal current) =>
        reference <= 0 ? decimal.MaxValue : Math.Max(0, (reference - current) / reference * 100m);

    private bool Valid(AccountSnapshot account, SymbolSpecification symbol, TradeSetup setup, decimal spread) =>
        account.Equity > 0 && account.Balance > 0 && account.DayStartEquity > 0 &&
        account.WeekStartEquity > 0 && account.PeakBalance > 0 && account.PeakEquity > 0 &&
        account.OpenPositions >= 0 && account.OpenRiskAmount >= 0 && symbol.IsValid && spread >= 0 &&
        setup.Symbol == symbol.CanonicalSymbol && setup.EntryPrice > 0 && setup.StopLoss > 0 && setup.TakeProfit > 0 &&
        setup.StopDistance > 0 && setup.StopDistance >= symbol.MinStopDistance &&
        (setup.Direction == TradeDirection.Buy
            ? setup.StopLoss < setup.EntryPrice && setup.TakeProfit > setup.EntryPrice
            : setup.StopLoss > setup.EntryPrice && setup.TakeProfit < setup.EntryPrice);

    private static RiskAssessment Reject(string reason) => new(false, reason);
}
