using GoldAiTrader.Core;
using GoldAiTrader.Execution;

namespace GoldAiTrader.Adapters.MT5;

public static class MT5BridgeProtocol
{
    public const string CurrentVersion = "1.0";

    public static void ValidateVersion(string protocolVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);
        if (!string.Equals(protocolVersion, CurrentVersion, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"MT5 bridge protocol version '{protocolVersion}' is incompatible with '{CurrentVersion}'.");
        }
    }

    public static void ValidateIdentity(MT5BridgeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.BridgeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.TerminalInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.BrokerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.AccountIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.AdapterVersion);
        ValidateVersion(identity.ProtocolVersion);
    }

    public static void ValidateEnvelope(MT5MessageEnvelope envelope,
        MT5BridgeIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateVersion(envelope.ProtocolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.BridgeInstanceId);
        if (!string.Equals(envelope.BridgeInstanceId, expectedIdentity.BridgeInstanceId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MT5 bridge message identity does not match the configured bridge instance.");
        }

        ValidateUtc(envelope.SentAtUtc, nameof(envelope.SentAtUtc));
    }

    public static void ValidateUtc(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp == default || timestamp.Offset != TimeSpan.Zero)
            throw new ArgumentException("MT5 bridge timestamps must be non-default UTC values.", parameterName);
    }
}

public sealed record MT5LoopbackEndpoint
{
    public MT5LoopbackEndpoint(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parsed.Host, "127.0.0.1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
            throw new ArgumentException(
                "MT5 transport endpoints must use http://127.0.0.1 and must not embed credentials in the URI.",
                nameof(address));
        Address = parsed;
    }

    public Uri Address { get; }
}

public sealed record MT5BridgeIdentity(string BridgeInstanceId, string TerminalInstanceId,
    string BrokerName, string AccountIdentifier, string AdapterVersion, string ProtocolVersion);

public sealed record MT5MessageEnvelope(string ProtocolVersion, string BridgeInstanceId,
    DateTimeOffset SentAtUtc);

public sealed record MT5HeartbeatMessage(MT5MessageEnvelope Envelope, bool TerminalConnected);

public sealed record MT5ConnectionStateMessage(MT5MessageEnvelope Envelope, bool TerminalConnected,
    string? Detail = null);

public sealed record MT5AccountDescription(string AccountIdentifier, string AccountCurrency,
    decimal Balance, decimal Equity, decimal DayStartEquity, decimal WeekStartEquity,
    decimal PeakBalance, decimal PeakEquity, int OpenPositions, decimal OpenRiskAmount,
    decimal FreeMargin, AccountEnvironment Environment, bool EmergencyShutdown = false);

public sealed record MT5AccountMessage(MT5MessageEnvelope Envelope, MT5AccountDescription Account);

public sealed record MT5SymbolDescription(string BrokerSymbol, string CanonicalSymbol,
    decimal TickSize, decimal TickValuePerVolumeUnit, decimal ContractSize,
    decimal MinVolume, decimal MaxVolume, decimal VolumeStep, decimal MinStopDistance,
    bool IsTradingAvailable);

public sealed record MT5SymbolMessage(MT5MessageEnvelope Envelope, MT5SymbolDescription Symbol);

public sealed record MT5PositionDescription(string PositionId, string BrokerSymbol,
    string CanonicalSymbol, TradeDirection Direction, decimal Volume, decimal EntryPrice,
    decimal StopLoss, decimal? TakeProfit, decimal CurrentRiskAmount, Guid? SignalId = null,
    string? ClientCorrelationId = null, string? StrategyVersion = null,
    string? OwnershipTag = null);

public sealed record MT5PositionInventoryMessage(MT5MessageEnvelope Envelope,
    string AccountIdentifier, IReadOnlyList<MT5PositionDescription> Positions);

public sealed record MT5CompletedBarDescription(string BrokerSymbol, string CanonicalSymbol,
    TimeSpan Timeframe, DateTimeOffset OpenTimeUtc, decimal Open, decimal High, decimal Low,
    decimal Close, decimal Bid, decimal Ask, decimal Volume = 0m);

public sealed record MT5CompletedBarMessage(MT5MessageEnvelope Envelope,
    MT5CompletedBarDescription Bar);

public abstract record MT5ExecutionCommand(MT5MessageEnvelope Envelope, Guid CommandId,
    string AccountIdentifier);

public sealed record MT5SubmitMarketOrderCommand(MT5MessageEnvelope Envelope, Guid CommandId,
    string AccountIdentifier, Guid SignalId, string ClientCorrelationId, string StrategyVersion,
    string OwnershipTag, string CanonicalSymbol, string BrokerSymbol, TradeDirection Direction,
    decimal Volume, decimal EntryPrice, decimal StopLoss, decimal TakeProfit)
    : MT5ExecutionCommand(Envelope, CommandId, AccountIdentifier);

public sealed record MT5ClosePositionCommand(MT5MessageEnvelope Envelope, Guid CommandId,
    string AccountIdentifier, string PositionId, Guid SignalId, string ClientCorrelationId,
    string StrategyVersion, string OwnershipTag)
    : MT5ExecutionCommand(Envelope, CommandId, AccountIdentifier);

public sealed record MT5ModifyPositionCommand(MT5MessageEnvelope Envelope, Guid CommandId,
    string AccountIdentifier, string PositionId, Guid SignalId, string ClientCorrelationId,
    string StrategyVersion, string OwnershipTag, decimal StopLoss, decimal TakeProfit)
    : MT5ExecutionCommand(Envelope, CommandId, AccountIdentifier);

public sealed record MT5ExecutionAcknowledgement(MT5MessageEnvelope Envelope, Guid CommandId,
    ExecutionResult Result);
