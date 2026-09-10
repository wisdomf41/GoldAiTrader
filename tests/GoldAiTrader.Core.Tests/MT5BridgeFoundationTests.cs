using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Platform;

namespace GoldAiTrader.Core.Tests;

public sealed class MT5BridgeFoundationTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-07T12:00:00Z");

    [Fact]
    public void MT5GatewayImplementsUniversalPlatformContract()
    {
        var (state, clock) = ReadyState();
        ITradingPlatformGateway gateway = new MT5PlatformGateway(state, timeProvider: clock);

        Assert.Equal("MetaTrader 5", gateway.Descriptor.Identity.PlatformName);
        Assert.NotNull(gateway.MarketDataProvider);
        Assert.NotNull(gateway.TradingAccountProvider);
        Assert.NotNull(gateway.SymbolSpecificationProvider);
        Assert.NotNull(gateway.PositionProvider);
        Assert.NotNull(gateway.ExecutionGateway);
    }

    [Fact]
    public async Task UnknownAccountEnvironmentRemainsFailClosed()
    {
        var (state, clock) = ReadyState(AccountEnvironment.Unknown);
        var gateway = new MT5PlatformGateway(state, timeProvider: clock);

        var account = await gateway.ExecutionGateway!.GetExecutionAccountAsync(default);

        Assert.Equal(AccountEnvironment.Unknown, account.Environment);
        Assert.False(account.IsVerifiedDemo);
    }

    [Fact]
    public async Task ExplicitDemoEnvironmentMapsToExistingAccountModel()
    {
        var (state, clock) = ReadyState(AccountEnvironment.Demo);
        var gateway = new MT5PlatformGateway(state, timeProvider: clock);

        var account = await gateway.TradingAccountProvider!.GetAccountAsync();

        Assert.Equal(AccountEnvironment.Demo, account.Environment);
        Assert.True(account.IsExplicitDemo);
        Assert.Equal("account-42", account.AccountIdentifier);
    }

    [Fact]
    public async Task LiveEnvironmentNeverBecomesExecutable()
    {
        var (state, clock) = ReadyState(AccountEnvironment.Live);
        var gateway = new MT5PlatformGateway(state, timeProvider: clock);

        var account = await gateway.ExecutionGateway!.GetExecutionAccountAsync(default);
        var suitability = PlatformCapabilityValidator.Validate(gateway, ExecutionMode.Live);

        Assert.Equal(AccountEnvironment.Live, account.Environment);
        Assert.False(account.IsVerifiedDemo);
        Assert.False(suitability.IsSuitable);
        Assert.Contains(suitability.BlockingReasons,
            reason => reason.Contains("Live", StringComparison.Ordinal));
    }

    [Fact]
    public void ArbitraryBrokerIdentityNeedsNoCoreChanges()
    {
        var identity = Identity("Independent Metals Broker");
        var state = new MT5BridgeState(identity, Mappings());
        var gateway = new MT5PlatformGateway(state);

        Assert.Equal("Independent Metals Broker", gateway.Descriptor.Identity.BrokerName);
        Assert.Equal(identity.BridgeInstanceId, gateway.Descriptor.Identity.ConnectionIdentifier);
    }

    [Fact]
    public async Task MT5SymbolNormalizationPreservesCanonicalAndBrokerSymbols()
    {
        var (state, clock) = ReadyState();
        var provider = new MT5SymbolSpecificationProvider(state);

        var specification = await provider.GetSpecificationAsync("XAUUSD");

        Assert.Equal("XAUUSD", specification.CanonicalSymbol);
        Assert.Equal("METAL.demo-7", specification.BrokerSymbol);
    }

    [Fact]
    public void InvalidSymbolSpecificationFailsClosed()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock);
        state.AcceptHeartbeat(Heartbeat(state, clock));
        var invalid = Symbol() with { TickSize = 0m };

        Assert.Throws<InvalidOperationException>(() =>
            state.AcceptSymbol(new(Envelope(state, clock), invalid)));
    }

    [Fact]
    public void FreshHeartbeatIsHealthy()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock, TimeSpan.FromSeconds(10));

        state.AcceptHeartbeat(Heartbeat(state, clock));

        Assert.True(state.GetHealth().Healthy);
    }

    [Fact]
    public void HeartbeatBecomesUnhealthyWhenStale()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock, TimeSpan.FromSeconds(10));
        state.AcceptHeartbeat(Heartbeat(state, clock));

        clock.Advance(TimeSpan.FromSeconds(11));

        var health = state.GetHealth();
        Assert.False(health.Healthy);
        Assert.Contains("stale", health.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviouslyHealthyBridgeDoesNotRetainReadyStatusAfterStaleness()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock, TimeSpan.FromSeconds(10));
        ApplyReadySnapshot(state, clock, AccountEnvironment.Demo);
        var gateway = new MT5PlatformGateway(state, timeProvider: clock);

        Assert.True((await gateway.GetStatusAsync()).Connected);
        clock.Advance(TimeSpan.FromSeconds(11));

        var status = await gateway.GetStatusAsync();
        var account = await gateway.ExecutionGateway!.GetExecutionAccountAsync(default);
        Assert.False(status.Connected);
        Assert.False(account.BrokerConnected);
    }

    [Fact]
    public async Task StaleBridgeStateProvidersFailClosed()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock, TimeSpan.FromSeconds(10));
        ApplyReadySnapshot(state, clock, AccountEnvironment.Demo);
        clock.Advance(TimeSpan.FromSeconds(11));
        var provider = new MT5TradingAccountProvider(state);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAccountAsync());

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccountProviderReadsLatestTrustedBridgeState()
    {
        var (state, clock) = ReadyState();
        clock.Advance(TimeSpan.FromSeconds(1));
        state.AcceptAccount(new(Envelope(state, clock),
            Account(AccountEnvironment.Demo) with { Balance = 1250m, Equity = 1240m }));
        var provider = new MT5TradingAccountProvider(state);

        var account = await provider.GetAccountAsync();

        Assert.Equal(1250m, account.Balance);
        Assert.Equal(1240m, account.Equity);
    }

    [Fact]
    public async Task PositionProviderReturnsBridgeInventory()
    {
        var (state, clock) = ReadyState();
        clock.Advance(TimeSpan.FromSeconds(1));
        state.AcceptPositions(new(Envelope(state, clock), state.Identity.AccountIdentifier,
            [OwnedPosition()]));
        var provider = new MT5PositionProvider(state);

        var positions = await provider.GetPositionsAsync();

        var position = Assert.Single(positions);
        Assert.Equal("mt5-position-1", position.PositionId);
        Assert.Equal(PositionOwnershipStatus.Owned, PositionOwnership.Classify(position));
    }

    [Fact]
    public async Task ManualPositionRemainsDistinguishableFromOwnedPosition()
    {
        var (state, clock) = ReadyState();
        clock.Advance(TimeSpan.FromSeconds(1));
        state.AcceptPositions(new(Envelope(state, clock), state.Identity.AccountIdentifier,
            [OwnedPosition() with
            {
                PositionId = "manual-position",
                SignalId = null,
                ClientCorrelationId = null,
                StrategyVersion = null,
                OwnershipTag = null
            }]));

        var position = Assert.Single(await new MT5PositionProvider(state).GetPositionsAsync());

        Assert.Equal(PositionOwnershipStatus.Unowned, PositionOwnership.Classify(position));
    }

    [Fact]
    public async Task CompletedBarFlowsToMarketDataProvider()
    {
        var (state, clock) = ReadyState();
        var timeframe = TimeSpan.FromMinutes(5);
        state.AcceptCompletedBar(new(Envelope(state, clock),
            new("METAL.demo-7", "XAUUSD", timeframe, Now - timeframe,
                2000m, 2002m, 1999m, 2001m, 2000.9m, 2001m, 42m)));
        var provider = new MT5MarketDataProvider(state);

        await using var bars = provider.StreamBarsAsync("XAUUSD", timeframe)
            .GetAsyncEnumerator();
        Assert.True(await bars.MoveNextAsync());
        Assert.Equal(Now - timeframe, bars.Current.OpenTime);
        Assert.Equal(2001m, bars.Current.Close);
    }

    [Fact]
    public void HistoricalBarsAreNotAdvertisedOrFaked()
    {
        var (state, clock) = ReadyState();
        var gateway = new MT5PlatformGateway(state, timeProvider: clock);

        Assert.Null(gateway.HistoricalMarketDataProvider);
        Assert.False(gateway.Descriptor.Capabilities.HasFlag(
            PlatformCapabilities.HistoricalBars));
    }

    [Fact]
    public async Task ExecutionFailsClosedWithoutConcreteTransport()
    {
        var (state, clock) = ReadyState();
        var gateway = new MT5PlatformGateway(state, timeProvider: clock);

        var result = await gateway.ExecutionGateway!.SubmitAsync(Order(), default);

        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Contains("unavailable", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(gateway.Descriptor.Capabilities.HasFlag(
            PlatformCapabilities.SubmitMarketOrders));
    }

    [Fact]
    public void MetadataCapabilitiesAreNotAdvertisedWithoutRoundTripSupport()
    {
        var (state, clock) = ReadyState();
        var gateway = new MT5PlatformGateway(state, timeProvider: clock);

        Assert.Null(gateway.ExecutionMetadataSupport);
        Assert.False(gateway.Descriptor.Capabilities.HasFlag(
            PlatformCapabilities.ClientCorrelationIds));
        Assert.False(gateway.Descriptor.Capabilities.HasFlag(
            PlatformCapabilities.OwnershipMetadata));
    }

    [Fact]
    public void ProtocolVersionMismatchIsRejected()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock);
        var message = new MT5HeartbeatMessage(
            Envelope(state, clock, protocolVersion: "99.0"), true);

        Assert.Throws<NotSupportedException>(() => state.AcceptHeartbeat(message));
    }

    [Fact]
    public void MissingBridgeIdentityIsRejected()
    {
        var invalid = Identity() with { BridgeInstanceId = "" };

        Assert.Throws<ArgumentException>(() =>
            new MT5BridgeState(invalid, Mappings()));
    }

    [Fact]
    public void MessageFromDifferentBridgeInstanceIsRejected()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock);
        var message = new MT5HeartbeatMessage(
            Envelope(state, clock, bridgeInstanceId: "other-bridge"), true);

        Assert.Throws<InvalidOperationException>(() => state.AcceptHeartbeat(message));
    }

    [Fact]
    public void LoopbackEndpointAcceptsOnlyExplicitIpv4LoopbackHttp()
    {
        var endpoint = new MT5LoopbackEndpoint("http://127.0.0.1:5088/mt5");

        Assert.Equal("127.0.0.1", endpoint.Address.Host);
        Assert.Throws<ArgumentException>(() =>
            new MT5LoopbackEndpoint("http://0.0.0.0:5088/mt5"));
        Assert.Throws<ArgumentException>(() =>
            new MT5LoopbackEndpoint("http://localhost:5088/mt5"));
        Assert.Throws<ArgumentException>(() =>
            new MT5LoopbackEndpoint("http://127.0.0.1:5088/mt5?mode=test"));
    }

    [Fact]
    public void ReplayedHeartbeatCannotRestoreFreshness()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock, TimeSpan.FromSeconds(10));
        var heartbeat = Heartbeat(state, clock);
        state.AcceptHeartbeat(heartbeat);
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Throws<InvalidOperationException>(() => state.AcceptHeartbeat(heartbeat));
        Assert.False(state.GetHealth().Healthy);
    }

    [Fact]
    public void ConnectionMessageCannotRefreshAStaleHeartbeat()
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock, TimeSpan.FromSeconds(10));
        state.AcceptHeartbeat(Heartbeat(state, clock));
        clock.Advance(TimeSpan.FromSeconds(11));

        state.AcceptConnectionState(new(Envelope(state, clock), true));

        Assert.False(state.GetHealth().Healthy);
    }

    private static (MT5BridgeState State, ManualTimeProvider Clock) ReadyState(
        AccountEnvironment environment = AccountEnvironment.Demo)
    {
        var clock = new ManualTimeProvider(Now);
        var state = State(clock);
        ApplyReadySnapshot(state, clock, environment);
        return (state, clock);
    }

    private static void ApplyReadySnapshot(MT5BridgeState state,
        ManualTimeProvider clock, AccountEnvironment environment)
    {
        state.AcceptHeartbeat(Heartbeat(state, clock));
        state.AcceptAccount(new(Envelope(state, clock), Account(environment)));
        state.AcceptSymbol(new(Envelope(state, clock), Symbol()));
        state.AcceptPositions(new(Envelope(state, clock),
            state.Identity.AccountIdentifier, []));
    }

    private static MT5BridgeState State(ManualTimeProvider clock,
        TimeSpan? heartbeatFreshness = null) =>
        new(Identity(), Mappings(), new(heartbeatFreshness), clock);

    private static MT5BridgeIdentity Identity(string brokerName = "Example Broker") =>
        new("bridge-a", "terminal-a", brokerName, "account-42", "1.0.0",
            MT5BridgeProtocol.CurrentVersion);

    private static IReadOnlyDictionary<string, string> Mappings() =>
        new Dictionary<string, string> { ["XAUUSD"] = "METAL.demo-7" };

    private static MT5MessageEnvelope Envelope(MT5BridgeState state,
        ManualTimeProvider? clock = null, string? protocolVersion = null,
        string? bridgeInstanceId = null) =>
        new(protocolVersion ?? MT5BridgeProtocol.CurrentVersion,
            bridgeInstanceId ?? state.Identity.BridgeInstanceId,
            (clock?.GetUtcNow() ?? Now).ToUniversalTime());

    private static MT5HeartbeatMessage Heartbeat(MT5BridgeState state,
        ManualTimeProvider clock) => new(Envelope(state, clock), true);

    private static MT5AccountDescription Account(AccountEnvironment environment) =>
        new("account-42", "USD", 1000m, 1000m, 1000m, 1000m,
            1000m, 1000m, 0, 0m, 900m, environment);

    private static MT5SymbolDescription Symbol() =>
        new("METAL.demo-7", "XAUUSD", 0.01m, 1m, 100m,
            0.01m, 100m, 0.01m, 0.1m, true);

    private static MT5PositionDescription OwnedPosition()
    {
        var signalId = Guid.NewGuid();
        return new("mt5-position-1", "METAL.demo-7", "XAUUSD",
            TradeDirection.Buy, 1m, 2000m, 1999m, 2002m, 1m,
            signalId, $"GoldAiTrader-{signalId:N}", "v1",
            PositionOwnership.DefaultOwnershipTag);
    }

    private static ApprovedOrder Order() =>
        new(new(Guid.NewGuid(), Now, "XAUUSD", TradeDirection.Buy,
            2000m, 1999m, 2002m, "v1", "test"), new(1m, 1m));

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
