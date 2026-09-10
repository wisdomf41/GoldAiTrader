namespace GoldAiTrader.Core;

public enum TradeDirection { Buy, Sell }
public enum StrategyAction { Buy, Sell, Wait }
public enum MarketSession { Closed, London, NewYork, LondonNewYorkOverlap }
public enum MarketRegime { Unknown, Trending, Ranging, ExcessiveVolatility }
public enum AccountEnvironment { Unknown, Demo, Live }

public sealed record MarketBar(DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Bid, decimal Ask, decimal Volume = 0)
{
    public decimal Spread => Ask - Bid;
    public void Validate()
    {
        if (OpenTime == default || Open <= 0 || High <= 0 || Low <= 0 || Close <= 0 || Bid <= 0 || Ask <= 0)
            throw new ArgumentException("Bar contains non-positive or missing data.");
        if (Low > High || Open < Low || Open > High || Close < Low || Close > High || Ask < Bid)
            throw new ArgumentException("Bar price relationships are invalid.");
    }
}

public sealed record MarketFeatures(DateTimeOffset Time, string Symbol, decimal Bid, decimal Ask,
    decimal FastEma, decimal SlowEma, decimal ContextFastEma, decimal ContextSlowEma,
    decimal Atr, decimal Rsi, decimal Adx, decimal Volatility, MarketSession Session, MarketRegime Regime)
{
    public decimal Spread => Ask - Bid;
    public bool IsValid => Time != default && !string.IsNullOrWhiteSpace(Symbol) && Bid > 0 && Ask >= Bid &&
        FastEma > 0 && SlowEma > 0 && ContextFastEma > 0 && ContextSlowEma > 0 && Atr > 0 &&
        Rsi is >= 0 and <= 100 && Adx is >= 0 and <= 100 && Volatility >= 0;
}

public sealed record TradeSetup(Guid SignalId, DateTimeOffset Time, string Symbol, TradeDirection Direction,
    decimal EntryPrice, decimal StopLoss, decimal TakeProfit, string StrategyVersion, string Reason)
{
    public decimal StopDistance => Math.Abs(EntryPrice - StopLoss);
    public decimal RewardDistance => Math.Abs(TakeProfit - EntryPrice);
}

public sealed record StrategyDecision(StrategyAction Action, string Reason, TradeSetup? Setup = null)
{
    public static StrategyDecision Wait(string reason) => new(StrategyAction.Wait, reason);
}

public sealed record AccountSnapshot(decimal Balance, decimal Equity, decimal DayStartEquity, decimal WeekStartEquity,
    decimal PeakBalance, decimal PeakEquity, int OpenPositions, decimal OpenRiskAmount, bool EmergencyShutdown = false,
    string AccountIdentifier = "", string AccountCurrency = "USD", decimal FreeMargin = 0m,
    AccountEnvironment Environment = AccountEnvironment.Unknown)
{
    public bool IsExplicitDemo => Environment == AccountEnvironment.Demo;
}

public sealed record SymbolSpecification(decimal TickSize, decimal TickValuePerVolumeUnit,
    decimal MinVolume, decimal MaxVolume, decimal VolumeStep, decimal MinStopDistance,
    string BrokerSymbol = "", string CanonicalSymbol = "XAUUSD", decimal ContractSize = 1m,
    bool IsTradingAvailable = true)
{
    public bool IsValid => TickSize > 0 && TickValuePerVolumeUnit > 0 && ContractSize > 0 && MinVolume > 0 &&
        MaxVolume >= MinVolume && VolumeStep > 0 && MinStopDistance >= 0 && IsTradingAvailable &&
        !string.IsNullOrWhiteSpace(CanonicalSymbol);
}

public sealed record PositionSize(decimal Volume, decimal PlannedRiskAmount);
public sealed record RiskAssessment(bool Approved, string Reason, PositionSize? PositionSize = null, bool Warning = false);

public sealed record PositionSnapshot(string PositionId, string CanonicalSymbol, string BrokerSymbol,
    TradeDirection Direction, decimal Volume, decimal EntryPrice, decimal StopLoss, decimal? TakeProfit,
    decimal CurrentRiskAmount, Guid? SignalId = null, string? ClientCorrelationId = null,
    string? StrategyVersion = null, string? OwnershipTag = null);

public interface IMarketDataProvider
{
    IAsyncEnumerable<MarketBar> StreamBarsAsync(string canonicalSymbol, TimeSpan timeframe, CancellationToken cancellationToken = default);
}

public interface ITradingAccountProvider
{
    Task<AccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default);
}

public interface ISymbolSpecificationProvider
{
    Task<SymbolSpecification> GetSpecificationAsync(string canonicalSymbol, CancellationToken cancellationToken = default);
}

public interface IPositionProvider
{
    Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default);
}
