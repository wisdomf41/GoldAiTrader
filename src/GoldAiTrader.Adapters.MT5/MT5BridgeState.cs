using System.Collections.Concurrent;
using System.Threading.Channels;
using GoldAiTrader.Core;

namespace GoldAiTrader.Adapters.MT5;

public sealed record MT5BridgeOptions(TimeSpan? HeartbeatFreshness = null,
    TimeSpan? SnapshotFreshness = null)
{
    public TimeSpan EffectiveHeartbeatFreshness => HeartbeatFreshness ?? TimeSpan.FromSeconds(15);

    public TimeSpan EffectiveSnapshotFreshness =>
        SnapshotFreshness ?? EffectiveHeartbeatFreshness;

    public void Validate()
    {
        if (EffectiveHeartbeatFreshness <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(HeartbeatFreshness));
        if (EffectiveSnapshotFreshness <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SnapshotFreshness));
    }
}

public sealed record MT5BridgeHealth(bool Healthy, string Reason,
    DateTimeOffset? LastHeartbeatReceivedAtUtc, TimeSpan HeartbeatFreshness);

public sealed record MT5ExecutionSnapshot(bool Ready, string Reason,
    AccountSnapshot? Account);

public sealed class MT5SymbolMapper
{
    private readonly IReadOnlyDictionary<string, string> brokerSymbols;

    public MT5SymbolMapper(IReadOnlyDictionary<string, string> brokerSymbols)
    {
        ArgumentNullException.ThrowIfNull(brokerSymbols);
        if (brokerSymbols.Any(mapping =>
                string.IsNullOrWhiteSpace(mapping.Key) || string.IsNullOrWhiteSpace(mapping.Value)))
            throw new ArgumentException("MT5 symbol mappings require non-empty canonical and broker symbols.",
                nameof(brokerSymbols));
        this.brokerSymbols = new Dictionary<string, string>(brokerSymbols, StringComparer.Ordinal);
    }

    public string GetBrokerSymbol(string canonicalSymbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSymbol);
        return brokerSymbols.TryGetValue(canonicalSymbol, out var brokerSymbol) &&
            !string.IsNullOrWhiteSpace(brokerSymbol)
                ? brokerSymbol
                : throw new KeyNotFoundException(
                    $"No MT5 symbol mapping exists for canonical symbol '{canonicalSymbol}'.");
    }

    public SymbolSpecification Normalize(MT5SymbolDescription source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.CanonicalSymbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.BrokerSymbol);

        var expectedBrokerSymbol = GetBrokerSymbol(source.CanonicalSymbol);
        if (!string.Equals(expectedBrokerSymbol, source.BrokerSymbol, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The MT5 symbol does not match the configured canonical mapping.");

        var specification = new SymbolSpecification(source.TickSize,
            source.TickValuePerVolumeUnit, source.MinVolume, source.MaxVolume,
            source.VolumeStep, source.MinStopDistance, source.BrokerSymbol,
            source.CanonicalSymbol, source.ContractSize, source.IsTradingAvailable);
        if (!specification.IsValid)
            throw new InvalidOperationException(
                "The MT5 symbol specification is incomplete, invalid, or unavailable for trading.");

        return specification;
    }
}

public sealed class MT5BridgeState
{
    private readonly object sync = new();
    private readonly MT5BridgeOptions options;
    private readonly TimeProvider timeProvider;
    private readonly MT5SymbolMapper symbolMapper;
    private readonly Dictionary<string, SymbolSpecification> specifications =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<MarketStreamKey, Channel<MarketBar>> streams = new();
    private readonly Dictionary<string, DateTimeOffset> lastSymbolSentAtUtc =
        new(StringComparer.Ordinal);
    private readonly Dictionary<MarketStreamKey, MarketBar> latestBars = [];
    private readonly Dictionary<MarketStreamKey, DateTimeOffset> lastBarOpenTimeUtc = [];
    private readonly Dictionary<MarketStreamKey, DateTimeOffset> lastBarSentAtUtc = [];
    private AccountSnapshot? account;
    private PositionSnapshot[]? positions;
    private bool terminalConnected;
    private DateTimeOffset? sessionStartedAtUtc;
    private DateTimeOffset? lastConnectivitySentAtUtc;
    private DateTimeOffset? lastHeartbeatSentAtUtc;
    private DateTimeOffset? lastHeartbeatReceivedAtUtc;
    private DateTimeOffset? lastAccountSentAtUtc;
    private DateTimeOffset? lastAccountReceivedAtUtc;
    private DateTimeOffset? lastPositionsSentAtUtc;
    private DateTimeOffset? lastPositionsReceivedAtUtc;

    public MT5BridgeState(MT5BridgeIdentity identity,
        IReadOnlyDictionary<string, string> symbolMappings,
        MT5BridgeOptions? options = null, TimeProvider? timeProvider = null)
    {
        MT5BridgeProtocol.ValidateIdentity(identity);
        ArgumentNullException.ThrowIfNull(symbolMappings);
        this.options = options ?? new();
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        symbolMapper = new(symbolMappings);
        Identity = identity;
        CanonicalSymbols = Array.AsReadOnly(
            symbolMappings.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    public MT5BridgeIdentity Identity { get; }
    public IReadOnlyList<string> CanonicalSymbols { get; }

    public void AcceptHeartbeat(MT5HeartbeatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message.Envelope);
        var receivedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        lock (sync)
        {
            ValidateMessageAgeLocked(message.Envelope.SentAtUtc,
                options.EffectiveHeartbeatFreshness, "heartbeat");
            EnsureNewer(message.Envelope.SentAtUtc, lastHeartbeatSentAtUtc, "heartbeat");
            EnsureNewer(message.Envelope.SentAtUtc, lastConnectivitySentAtUtc,
                "connectivity message");
            SetConnectivityLocked(message.TerminalConnected, message.Envelope.SentAtUtc);
            lastHeartbeatSentAtUtc = message.Envelope.SentAtUtc;
            lastHeartbeatReceivedAtUtc = message.TerminalConnected ? receivedAtUtc : null;
        }
    }

    public void AcceptConnectionState(MT5ConnectionStateMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message.Envelope);
        lock (sync)
        {
            ValidateMessageAgeLocked(message.Envelope.SentAtUtc,
                options.EffectiveHeartbeatFreshness, "connection state");
            EnsureNewer(message.Envelope.SentAtUtc, lastConnectivitySentAtUtc,
                "connection state");
            SetConnectivityLocked(message.TerminalConnected, message.Envelope.SentAtUtc);
        }
    }

    public void AcceptAccount(MT5AccountMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message.Envelope);
        ValidateAccount(message.Account);
        var normalized = new AccountSnapshot(message.Account.Balance, message.Account.Equity,
            message.Account.DayStartEquity, message.Account.WeekStartEquity,
            message.Account.PeakBalance, message.Account.PeakEquity,
            message.Account.OpenPositions, message.Account.OpenRiskAmount,
            message.Account.EmergencyShutdown, message.Account.AccountIdentifier,
            message.Account.AccountCurrency, message.Account.FreeMargin,
            message.Account.Environment);
        lock (sync)
        {
            ValidateSessionMessageLocked(message.Envelope.SentAtUtc, "account");
            EnsureNewer(message.Envelope.SentAtUtc, lastAccountSentAtUtc, "account");
            account = normalized;
            lastAccountSentAtUtc = message.Envelope.SentAtUtc;
            lastAccountReceivedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        }
    }

    public void AcceptSymbol(MT5SymbolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message.Envelope);
        var normalized = symbolMapper.Normalize(message.Symbol);
        lock (sync)
        {
            ValidateSessionMessageLocked(message.Envelope.SentAtUtc, "symbol specification");
            lastSymbolSentAtUtc.TryGetValue(normalized.CanonicalSymbol, out var lastSentAtUtc);
            EnsureNewer(message.Envelope.SentAtUtc,
                lastSentAtUtc == default ? null : lastSentAtUtc, "symbol specification");
            specifications[normalized.CanonicalSymbol] = normalized;
            lastSymbolSentAtUtc[normalized.CanonicalSymbol] = message.Envelope.SentAtUtc;
        }
    }

    public void AcceptPositions(MT5PositionInventoryMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Positions);
        Validate(message.Envelope);
        if (!string.Equals(message.AccountIdentifier, Identity.AccountIdentifier,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "MT5 position inventory belongs to a different account.");

        lock (sync)
        {
            ValidateSessionMessageLocked(message.Envelope.SentAtUtc, "position inventory");
            EnsureNewer(message.Envelope.SentAtUtc, lastPositionsSentAtUtc,
                "position inventory");
            positions = message.Positions.Select(NormalizePositionLocked).ToArray();
            lastPositionsSentAtUtc = message.Envelope.SentAtUtc;
            lastPositionsReceivedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        }
    }

    public void AcceptCompletedBar(MT5CompletedBarMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Bar);
        Validate(message.Envelope);
        var source = message.Bar;
        MT5BridgeProtocol.ValidateUtc(source.OpenTimeUtc, nameof(source.OpenTimeUtc));
        if (source.Timeframe <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(source.Timeframe));
        var bar = new MarketBar(source.OpenTimeUtc, source.Open, source.High,
            source.Low, source.Close, source.Bid, source.Ask, source.Volume);
        bar.Validate();
        var key = new MarketStreamKey(source.CanonicalSymbol, source.Timeframe);
        lock (sync)
        {
            ValidateSessionMessageLocked(message.Envelope.SentAtUtc, "completed bar");
            if (!specifications.TryGetValue(source.CanonicalSymbol, out var specification))
                throw new InvalidOperationException(
                    "MT5 completed bar has no trusted symbol specification.");
            if (!string.Equals(specification.BrokerSymbol, source.BrokerSymbol,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "MT5 completed bar symbol does not match the trusted canonical mapping.");
            if (source.OpenTimeUtc + source.Timeframe > message.Envelope.SentAtUtc ||
                source.OpenTimeUtc + source.Timeframe > timeProvider.GetUtcNow())
                throw new InvalidOperationException(
                    "MT5 completed bar interval has not closed.");
            lastBarSentAtUtc.TryGetValue(key, out var lastSentAtUtc);
            EnsureNewer(message.Envelope.SentAtUtc,
                lastSentAtUtc == default ? null : lastSentAtUtc, "completed bar");
            if (lastBarOpenTimeUtc.TryGetValue(key, out var lastOpenTimeUtc) &&
                source.OpenTimeUtc <= lastOpenTimeUtc)
                throw new InvalidOperationException(
                    "MT5 completed bar is duplicated or older than the latest trusted bar.");

            latestBars[key] = bar;
            lastBarOpenTimeUtc[key] = source.OpenTimeUtc;
            lastBarSentAtUtc[key] = message.Envelope.SentAtUtc;
            if (!GetChannel(key).Writer.TryWrite(bar))
                throw new InvalidOperationException("MT5 completed bar stream is unavailable.");
        }
    }

    public MT5BridgeHealth GetHealth()
    {
        lock (sync)
            return GetHealthLocked(timeProvider.GetUtcNow());
    }

    public AccountSnapshot GetTrustedAccount()
    {
        lock (sync)
        {
            EnsureHealthyLocked();
            var trustedAccount = account ?? throw new InvalidOperationException(
                "MT5 account state has not been received.");
            EnsureFreshSnapshotLocked(lastAccountReceivedAtUtc, "account");
            return trustedAccount;
        }
    }

    public SymbolSpecification GetTrustedSpecification(string canonicalSymbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSymbol);
        lock (sync)
        {
            EnsureHealthyLocked();
            return specifications.TryGetValue(canonicalSymbol, out var specification)
                ? specification
                : throw new KeyNotFoundException(
                    $"No trusted MT5 symbol specification exists for '{canonicalSymbol}'.");
        }
    }

    public IReadOnlyList<PositionSnapshot> GetTrustedPositions()
    {
        lock (sync)
        {
            EnsureHealthyLocked();
            var trustedPositions = positions ?? throw new InvalidOperationException(
                "MT5 position inventory has not been received.");
            EnsureFreshSnapshotLocked(lastPositionsReceivedAtUtc, "position inventory");
            return trustedPositions.ToArray();
        }
    }

    public (MT5BridgeHealth Health, AccountSnapshot? Account) GetStatusSnapshot()
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var health = GetHealthLocked(now);
            var freshAccount = health.Healthy && account is not null &&
                IsFreshSnapshotLocked(lastAccountReceivedAtUtc, now)
                    ? account
                    : null;
            return (health, freshAccount);
        }
    }

    public MT5ExecutionSnapshot GetExecutionSnapshot()
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var health = GetHealthLocked(now);
            if (!health.Healthy)
                return new(false, health.Reason, null);
            if (account is null || !IsFreshSnapshotLocked(lastAccountReceivedAtUtc, now))
                return new(false, "MT5 account state is missing or stale.", null);
            if (positions is null || !IsFreshSnapshotLocked(lastPositionsReceivedAtUtc, now))
                return new(false, "MT5 position inventory is missing or stale.", account);

            return new(true, "MT5 execution snapshots are fresh for the current session.",
                account);
        }
    }

    public bool TryGetLatestCompletedBar(string canonicalSymbol, TimeSpan timeframe,
        out MarketBar? bar)
    {
        lock (sync)
        {
            EnsureHealthyLocked();
            return latestBars.TryGetValue(new(canonicalSymbol, timeframe), out bar);
        }
    }

    public async IAsyncEnumerable<MarketBar> StreamCompletedBarsAsync(string canonicalSymbol,
        TimeSpan timeframe,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSymbol);
        if (timeframe <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeframe));
        EnsureHealthy();
        var channel = GetChannel(new(canonicalSymbol, timeframe));
        await foreach (var bar in channel.Reader.ReadAllAsync(cancellationToken))
        {
            EnsureHealthy();
            yield return bar;
        }
    }

    private PositionSnapshot NormalizePositionLocked(MT5PositionDescription source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.PositionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.CanonicalSymbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.BrokerSymbol);
        if (!Enum.IsDefined(source.Direction) || source.Volume <= 0 ||
            source.EntryPrice <= 0 || source.StopLoss <= 0 ||
            source.TakeProfit is <= 0 || source.CurrentRiskAmount < 0)
            throw new InvalidOperationException("MT5 position contains invalid required values.");
        if (!specifications.TryGetValue(source.CanonicalSymbol, out var specification) ||
            !string.Equals(specification.BrokerSymbol, source.BrokerSymbol,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "MT5 position symbol does not match a trusted canonical mapping.");

        return new(source.PositionId, source.CanonicalSymbol, source.BrokerSymbol,
            source.Direction, source.Volume, source.EntryPrice, source.StopLoss,
            source.TakeProfit, source.CurrentRiskAmount, source.SignalId,
            NormalizeOptional(source.ClientCorrelationId),
            NormalizeOptional(source.StrategyVersion), NormalizeOptional(source.OwnershipTag));
    }

    private void ValidateAccount(MT5AccountDescription source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(source.AccountIdentifier, Identity.AccountIdentifier,
                StringComparison.Ordinal))
            throw new InvalidOperationException("MT5 account state belongs to a different account.");
        ArgumentException.ThrowIfNullOrWhiteSpace(source.AccountCurrency);
        if (source.Balance <= 0 || source.Equity <= 0 || source.DayStartEquity <= 0 ||
            source.WeekStartEquity <= 0 || source.PeakBalance <= 0 ||
            source.PeakEquity <= 0 || source.OpenPositions < 0 ||
            source.OpenRiskAmount < 0 || source.FreeMargin < 0 ||
            !Enum.IsDefined(source.Environment))
            throw new InvalidOperationException("MT5 account state contains invalid required values.");
    }

    private void EnsureHealthy()
    {
        lock (sync)
            EnsureHealthyLocked();
    }

    private void EnsureHealthyLocked()
    {
        var health = GetHealthLocked(timeProvider.GetUtcNow());
        if (!health.Healthy)
            throw new InvalidOperationException(health.Reason);
    }

    private MT5BridgeHealth GetHealthLocked(DateTimeOffset now)
    {
        if (!terminalConnected)
            return new(false, "MT5 terminal is disconnected.",
                lastHeartbeatReceivedAtUtc, options.EffectiveHeartbeatFreshness);
        if (lastHeartbeatReceivedAtUtc is null)
            return new(false, "MT5 bridge heartbeat has not been received.",
                null, options.EffectiveHeartbeatFreshness);

        var age = now - lastHeartbeatReceivedAtUtc.Value;
        if (age < TimeSpan.Zero || age > options.EffectiveHeartbeatFreshness)
            return new(false, "MT5 bridge heartbeat is stale.",
                lastHeartbeatReceivedAtUtc, options.EffectiveHeartbeatFreshness);
        return new(true, "MT5 bridge heartbeat is fresh.",
            lastHeartbeatReceivedAtUtc, options.EffectiveHeartbeatFreshness);
    }

    private void Validate(MT5MessageEnvelope envelope) =>
        MT5BridgeProtocol.ValidateEnvelope(envelope, Identity);

    private Channel<MarketBar> GetChannel(MarketStreamKey key) =>
        streams.GetOrAdd(key, _ => Channel.CreateUnbounded<MarketBar>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            }));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ValidateSessionMessageLocked(DateTimeOffset sentAtUtc, string stateName)
    {
        EnsureHealthyLocked();
        ValidateMessageAgeLocked(sentAtUtc, options.EffectiveSnapshotFreshness, stateName);
        if (sessionStartedAtUtc is null || sentAtUtc < sessionStartedAtUtc)
            throw new InvalidOperationException(
                $"MT5 {stateName} message predates the current connected session.");
    }

    private void ValidateMessageAgeLocked(DateTimeOffset sentAtUtc,
        TimeSpan freshness, string stateName)
    {
        var age = timeProvider.GetUtcNow().ToUniversalTime() - sentAtUtc;
        if (age < TimeSpan.Zero || age > freshness)
            throw new InvalidOperationException(
                $"MT5 {stateName} timestamp is outside the trusted freshness window.");
    }

    private static void EnsureNewer(DateTimeOffset sentAtUtc,
        DateTimeOffset? previousSentAtUtc, string stateName)
    {
        if (previousSentAtUtc is not null && sentAtUtc <= previousSentAtUtc)
            throw new InvalidOperationException(
                $"MT5 {stateName} message is duplicated or older than the latest trusted message.");
    }

    private void SetConnectivityLocked(bool connected, DateTimeOffset sentAtUtc)
    {
        if (!connected)
        {
            InvalidateSessionBoundStateLocked();
            terminalConnected = false;
            sessionStartedAtUtc = null;
            lastHeartbeatReceivedAtUtc = null;
        }
        else if (!terminalConnected)
        {
            InvalidateSessionBoundStateLocked();
            terminalConnected = true;
            sessionStartedAtUtc = sentAtUtc;
            lastHeartbeatReceivedAtUtc = null;
        }

        lastConnectivitySentAtUtc = sentAtUtc;
    }

    private void InvalidateSessionBoundStateLocked()
    {
        account = null;
        lastAccountReceivedAtUtc = null;
        positions = null;
        lastPositionsReceivedAtUtc = null;
        specifications.Clear();
        latestBars.Clear();

        foreach (var stream in streams.Values)
            stream.Writer.TryComplete();
        streams.Clear();
    }

    private void EnsureFreshSnapshotLocked(DateTimeOffset? receivedAtUtc, string stateName)
    {
        if (!IsFreshSnapshotLocked(receivedAtUtc,
                timeProvider.GetUtcNow().ToUniversalTime()))
            throw new InvalidOperationException($"MT5 {stateName} is missing or stale.");
    }

    private bool IsFreshSnapshotLocked(DateTimeOffset? receivedAtUtc, DateTimeOffset now)
    {
        if (receivedAtUtc is null)
            return false;
        var age = now - receivedAtUtc.Value;
        return age >= TimeSpan.Zero && age <= options.EffectiveSnapshotFreshness;
    }

    private readonly record struct MarketStreamKey(string CanonicalSymbol, TimeSpan Timeframe);
}
