using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Platform;

namespace GoldAiTrader.Core.Tests;

public sealed class MT5BridgeStateHardeningTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-07T12:00:00Z");

    [Fact]
    public async Task OlderAccountMessageCannotOverwriteNewerState()
    {
        var (state, clock) = ReadyState();
        clock.Advance(TimeSpan.FromSeconds(2));
        state.AcceptAccount(new(Envelope(state, clock.GetUtcNow()),
            Account() with { Balance = 2000m, Equity = 1990m }));

        var error = Assert.Throws<InvalidOperationException>(() =>
            state.AcceptAccount(new(Envelope(state, Now + TimeSpan.FromSeconds(1)),
                Account() with { Balance = 1500m, Equity = 1490m })));

        Assert.Contains("older", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2000m, (await new MT5TradingAccountProvider(state)
            .GetAccountAsync()).Balance);
    }

    [Fact]
    public async Task OlderPositionInventoryCannotOverwriteNewerState()
    {
        var (state, clock) = ReadyState();
        clock.Advance(TimeSpan.FromSeconds(2));
        state.AcceptPositions(new(Envelope(state, clock.GetUtcNow()),
            state.Identity.AccountIdentifier, [Position("new-position")]));

        var error = Assert.Throws<InvalidOperationException>(() =>
            state.AcceptPositions(new(Envelope(state, Now + TimeSpan.FromSeconds(1)),
                state.Identity.AccountIdentifier, [Position("old-position")])));

        Assert.Contains("older", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("new-position", Assert.Single(await new MT5PositionProvider(state)
            .GetPositionsAsync()).PositionId);
    }

    [Fact]
    public async Task OlderSymbolMessageCannotOverwriteNewerSpecification()
    {
        var (state, clock) = ReadyState();
        clock.Advance(TimeSpan.FromSeconds(2));
        state.AcceptSymbol(new(Envelope(state, clock.GetUtcNow()),
            Symbol() with { TickSize = 0.02m }));

        Assert.Throws<InvalidOperationException>(() =>
            state.AcceptSymbol(new(Envelope(state, Now + TimeSpan.FromSeconds(1)),
                Symbol() with { TickSize = 0.03m })));

        Assert.Equal(0.02m, (await new MT5SymbolSpecificationProvider(state)
            .GetSpecificationAsync("XAUUSD")).TickSize);
    }

    [Fact]
    public void DuplicateAndOutOfOrderCompletedBarsAreRejected()
    {
        var (state, clock) = ReadyState();
        var timeframe = TimeSpan.FromMinutes(5);
        clock.Advance(TimeSpan.FromSeconds(1));
        state.AcceptCompletedBar(BarMessage(state, clock.GetUtcNow(),
            Now - timeframe, timeframe));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Throws<InvalidOperationException>(() =>
            state.AcceptCompletedBar(BarMessage(state, clock.GetUtcNow(),
                Now - timeframe, timeframe)));
        Assert.Throws<InvalidOperationException>(() =>
            state.AcceptCompletedBar(BarMessage(state, clock.GetUtcNow(),
                Now - (timeframe * 2), timeframe)));

        Assert.True(state.TryGetLatestCompletedBar("XAUUSD", timeframe, out var latest));
        Assert.Equal(Now - timeframe, latest!.OpenTime);

        DisconnectAndReconnect(state, clock);
        state.AcceptSymbol(new(Envelope(state, clock.GetUtcNow()), Symbol()));
        Assert.Throws<InvalidOperationException>(() =>
            state.AcceptCompletedBar(BarMessage(state, clock.GetUtcNow(),
                Now - timeframe, timeframe)));
        Assert.False(state.TryGetLatestCompletedBar("XAUUSD", timeframe, out _));
    }

    [Fact]
    public void FutureOrUnclosedCompletedBarIsRejected()
    {
        var (state, clock) = ReadyState();
        var timeframe = TimeSpan.FromMinutes(5);

        var error = Assert.Throws<InvalidOperationException>(() =>
            state.AcceptCompletedBar(BarMessage(state, clock.GetUtcNow(),
                Now - TimeSpan.FromMinutes(4), timeframe)));

        Assert.Contains("not closed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(state.TryGetLatestCompletedBar("XAUUSD", timeframe, out _));
    }

    [Fact]
    public async Task DisconnectInvalidatesExecutableTrustedState()
    {
        var (state, clock) = ReadyState();
        clock.Advance(TimeSpan.FromSeconds(1));

        state.AcceptConnectionState(new(Envelope(state, clock.GetUtcNow()), false));

        Assert.False(state.GetExecutionSnapshot().Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MT5TradingAccountProvider(state).GetAccountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MT5PositionProvider(state).GetPositionsAsync());
    }

    [Fact]
    public async Task ReconnectDoesNotReusePreviousAccountOrPositions()
    {
        var (state, clock) = ReadyState();
        DisconnectAndReconnect(state, clock);

        Assert.True(state.GetHealth().Healthy);
        Assert.False(state.GetExecutionSnapshot().Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MT5TradingAccountProvider(state).GetAccountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MT5PositionProvider(state).GetPositionsAsync());
    }

    [Fact]
    public void PostReconnectReadinessRequiresAllFreshSessionSnapshots()
    {
        var (state, clock) = ReadyState();
        DisconnectAndReconnect(state, clock);

        state.AcceptAccount(new(Envelope(state, clock.GetUtcNow()), Account()));
        Assert.False(state.GetExecutionSnapshot().Ready);

        state.AcceptSymbol(new(Envelope(state, clock.GetUtcNow()), Symbol()));
        state.AcceptPositions(new(Envelope(state, clock.GetUtcNow()),
            state.Identity.AccountIdentifier, []));

        var readiness = state.GetExecutionSnapshot();
        Assert.True(readiness.Ready);
        Assert.Equal(AccountEnvironment.Demo, readiness.Account!.Environment);
    }

    [Fact]
    public void OldConnectionStateCannotOverrideNewerDisconnect()
    {
        var (state, clock) = ReadyState();
        clock.Advance(TimeSpan.FromSeconds(2));
        state.AcceptConnectionState(new(Envelope(state, clock.GetUtcNow()), false));

        Assert.Throws<InvalidOperationException>(() =>
            state.AcceptConnectionState(new(
                Envelope(state, Now + TimeSpan.FromSeconds(1)), true)));

        Assert.False(state.GetHealth().Healthy);
    }

    [Fact]
    public async Task FreshHeartbeatDoesNotTrustAncientExecutionSnapshots()
    {
        var clock = new ManualTimeProvider(Now);
        var state = new MT5BridgeState(Identity(), Mappings(),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)), clock);
        ApplyReadySnapshot(state, clock);
        clock.Advance(TimeSpan.FromSeconds(6));
        state.AcceptHeartbeat(new(Envelope(state, clock.GetUtcNow()), true));

        Assert.True(state.GetHealth().Healthy);
        Assert.False(state.GetExecutionSnapshot().Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MT5PositionProvider(state).GetPositionsAsync());
    }

    [Fact]
    public async Task RuntimeTransportUnavailabilityFailsReadinessWithoutChangingCapabilities()
    {
        var (state, clock) = ReadyState();
        var transport = new CapturingTransport();
        var gateway = new MT5PlatformGateway(state, transport, clock);
        Assert.True(gateway.Descriptor.Capabilities.HasFlag(
            PlatformCapabilities.SubmitMarketOrders));
        Assert.True((await gateway.ExecutionGateway!
            .GetExecutionAccountAsync(default)).BrokerConnected);

        transport.IsAvailable = false;

        Assert.False((await gateway.ExecutionGateway
            .GetExecutionAccountAsync(default)).BrokerConnected);
        var result = await gateway.ExecutionGateway.SubmitAsync(Order(), default);
        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Equal(0, transport.SendCount);
        Assert.True(gateway.Descriptor.Capabilities.HasFlag(
            PlatformCapabilities.SubmitMarketOrders));
    }

    [Fact]
    public async Task RuntimeTransportAuthenticationLossFailsReadiness()
    {
        var (state, clock) = ReadyState();
        var transport = new CapturingTransport();
        var gateway = new MT5PlatformGateway(state, transport, clock);
        transport.IsAuthenticated = false;

        Assert.False((await gateway.ExecutionGateway!
            .GetExecutionAccountAsync(default)).BrokerConnected);
        var result = await gateway.ExecutionGateway.SubmitAsync(Order(), default);
        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task SubmitCommandUsesDurableExecutionCorrelationConvention()
    {
        var (state, clock) = ReadyState();
        var transport = new CapturingTransport();
        var gateway = new MT5PlatformGateway(state, transport, clock);
        var signalId = Guid.Parse("3bb6b798-5370-4a1c-9745-4312cdd432cf");

        var result = await gateway.ExecutionGateway!.SubmitAsync(Order(signalId), default);

        Assert.Equal(OrderStatus.Accepted, result.Status);
        var command = Assert.IsType<MT5SubmitMarketOrderCommand>(transport.LastCommand);
        Assert.Equal($"GoldAiTrader-{signalId:N}", command.ClientCorrelationId);
        Assert.Equal(signalId, command.SignalId);
    }

    private static void DisconnectAndReconnect(MT5BridgeState state,
        ManualTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromSeconds(1));
        state.AcceptConnectionState(new(Envelope(state, clock.GetUtcNow()), false));
        clock.Advance(TimeSpan.FromSeconds(1));
        state.AcceptHeartbeat(new(Envelope(state, clock.GetUtcNow()), true));
    }

    private static (MT5BridgeState State, ManualTimeProvider Clock) ReadyState()
    {
        var clock = new ManualTimeProvider(Now);
        var state = new MT5BridgeState(Identity(), Mappings(), timeProvider: clock);
        ApplyReadySnapshot(state, clock);
        return (state, clock);
    }

    private static void ApplyReadySnapshot(MT5BridgeState state,
        ManualTimeProvider clock)
    {
        var envelope = Envelope(state, clock.GetUtcNow());
        state.AcceptHeartbeat(new(envelope, true));
        state.AcceptAccount(new(envelope, Account()));
        state.AcceptSymbol(new(envelope, Symbol()));
        state.AcceptPositions(new(envelope, state.Identity.AccountIdentifier, []));
    }

    private static MT5CompletedBarMessage BarMessage(MT5BridgeState state,
        DateTimeOffset sentAtUtc, DateTimeOffset openTimeUtc, TimeSpan timeframe) =>
        new(Envelope(state, sentAtUtc), new("METAL.demo-7", "XAUUSD", timeframe,
            openTimeUtc, 2000m, 2002m, 1999m, 2001m, 2000.9m, 2001m, 42m));

    private static MT5MessageEnvelope Envelope(MT5BridgeState state,
        DateTimeOffset sentAtUtc) =>
        new(MT5BridgeProtocol.CurrentVersion, state.Identity.BridgeInstanceId,
            sentAtUtc.ToUniversalTime());

    private static MT5BridgeIdentity Identity() =>
        new("bridge-a", "terminal-a", "Example Broker", "account-42", "1.0.0",
            MT5BridgeProtocol.CurrentVersion);

    private static IReadOnlyDictionary<string, string> Mappings() =>
        new Dictionary<string, string> { ["XAUUSD"] = "METAL.demo-7" };

    private static MT5AccountDescription Account() =>
        new("account-42", "USD", 1000m, 1000m, 1000m, 1000m,
            1000m, 1000m, 0, 0m, 900m, AccountEnvironment.Demo);

    private static MT5SymbolDescription Symbol() =>
        new("METAL.demo-7", "XAUUSD", 0.01m, 1m, 100m,
            0.01m, 100m, 0.01m, 0.1m, true);

    private static MT5PositionDescription Position(string positionId)
    {
        var signalId = Guid.NewGuid();
        return new(positionId, "METAL.demo-7", "XAUUSD", TradeDirection.Buy,
            1m, 2000m, 1999m, 2002m, 1m, signalId,
            $"GoldAiTrader-{signalId:N}", "v1", PositionOwnership.DefaultOwnershipTag);
    }

    private static ApprovedOrder Order(Guid? signalId = null) =>
        new(new(signalId ?? Guid.NewGuid(), Now, "XAUUSD", TradeDirection.Buy,
            2000m, 1999m, 2002m, "v1", "test"), new(1m, 1m));

    private sealed class CapturingTransport : IMT5LoopbackExecutionTransport
    {
        public MT5LoopbackEndpoint Endpoint { get; } =
            new("http://127.0.0.1:5088/mt5");
        public bool IsAvailable { get; set; } = true;
        public bool IsAuthenticated { get; set; } = true;
        public bool SupportsClientCorrelationIds => true;
        public bool SupportsOwnershipMetadata => true;
        public MT5ExecutionCommand? LastCommand { get; private set; }
        public int SendCount { get; private set; }

        public Task<MT5ExecutionAcknowledgement> SendAsync(MT5ExecutionCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCommand = command;
            SendCount++;
            return Task.FromResult(new MT5ExecutionAcknowledgement(command.Envelope,
                command.CommandId, new(OrderStatus.Accepted, "Captured by test.")));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
