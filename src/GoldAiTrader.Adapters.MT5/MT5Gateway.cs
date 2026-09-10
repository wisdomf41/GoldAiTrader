using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Platform;

namespace GoldAiTrader.Adapters.MT5;

public interface IMT5LoopbackExecutionTransport : IExecutionMetadataSupport
{
    MT5LoopbackEndpoint Endpoint { get; }
    bool IsAvailable { get; }
    bool IsAuthenticated { get; }

    Task<MT5ExecutionAcknowledgement> SendAsync(MT5ExecutionCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class MT5TradingAccountProvider(MT5BridgeState state) : ITradingAccountProvider
{
    public Task<AccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state.GetTrustedAccount());
    }
}

public sealed class MT5SymbolSpecificationProvider(MT5BridgeState state)
    : ISymbolSpecificationProvider
{
    public Task<SymbolSpecification> GetSpecificationAsync(string canonicalSymbol,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state.GetTrustedSpecification(canonicalSymbol));
    }
}

public sealed class MT5PositionProvider(MT5BridgeState state) : IPositionProvider
{
    public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state.GetTrustedPositions());
    }
}

public sealed class MT5MarketDataProvider(MT5BridgeState state) : IMarketDataProvider
{
    public IAsyncEnumerable<MarketBar> StreamBarsAsync(string canonicalSymbol,
        TimeSpan timeframe, CancellationToken cancellationToken = default) =>
        state.StreamCompletedBarsAsync(canonicalSymbol, timeframe, cancellationToken);
}

public sealed class MT5ExecutionGateway
    : IExecutionGateway
{
    private readonly MT5BridgeState state;
    private readonly IMT5LoopbackExecutionTransport? transport;
    private readonly TimeProvider timeProvider;

    public MT5ExecutionGateway(MT5BridgeState state,
        IMT5LoopbackExecutionTransport? transport = null,
        TimeProvider? timeProvider = null)
    {
        this.state = state;
        this.transport = transport;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ProviderName => "MetaTrader 5";

    public Task<ExecutionAccountState> GetExecutionAccountAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = state.GetExecutionSnapshot();
        var account = snapshot.Account;
        return Task.FromResult(new ExecutionAccountState(
            account?.AccountIdentifier ?? string.Empty,
            account?.Environment ?? AccountEnvironment.Unknown,
            snapshot.Ready && IsTransportReady()));
    }

    public async Task<ExecutionResult> SubmitAsync(ApprovedOrder order,
        CancellationToken cancellationToken)
    {
        if (!TryGetTransport(out var availableTransport, out var failure))
            return failure;

        var account = await GetExecutionAccountAsync(cancellationToken);
        if (!account.IsVerifiedDemo)
            return new(OrderStatus.Rejected,
                "MT5 execution requires a fresh, explicitly verified Demo account.");

        var specification = state.GetTrustedSpecification(order.Setup.Symbol);
        var command = new MT5SubmitMarketOrderCommand(CreateEnvelope(), Guid.NewGuid(),
            account.AccountIdentifier, order.Setup.SignalId,
            $"GoldAiTrader-{order.Setup.SignalId:N}", order.Setup.StrategyVersion,
            PositionOwnership.DefaultOwnershipTag, order.Setup.Symbol,
            specification.BrokerSymbol, order.Setup.Direction, order.PositionSize.Volume,
            order.Setup.EntryPrice, order.Setup.StopLoss, order.Setup.TakeProfit);
        return await SendAsync(availableTransport, command, cancellationToken);
    }

    public async Task<ExecutionResult> CloseAsync(string positionId,
        CancellationToken cancellationToken)
    {
        if (!TryGetTransport(out var availableTransport, out var failure))
            return failure;
        var account = await GetExecutionAccountAsync(cancellationToken);
        if (!account.IsVerifiedDemo)
            return new(OrderStatus.Rejected,
                "MT5 execution requires a fresh, explicitly verified Demo account.");

        var position = GetOwnedPosition(positionId);
        if (position is null)
            return new(OrderStatus.Rejected,
                "MT5 position is missing or is not unambiguously owned by GoldAiTrader.");
        var command = new MT5ClosePositionCommand(CreateEnvelope(), Guid.NewGuid(),
            account.AccountIdentifier, position.PositionId, position.SignalId!.Value,
            position.ClientCorrelationId!, position.StrategyVersion!, position.OwnershipTag!);
        return await SendAsync(availableTransport, command, cancellationToken);
    }

    public async Task<ExecutionResult> ModifyAsync(string positionId,
        decimal stopLoss, decimal takeProfit, CancellationToken cancellationToken)
    {
        if (!TryGetTransport(out var availableTransport, out var failure))
            return failure;
        if (stopLoss <= 0 || takeProfit <= 0)
            return new(OrderStatus.Rejected, "MT5 stop-loss and take-profit values must be positive.");

        var account = await GetExecutionAccountAsync(cancellationToken);
        if (!account.IsVerifiedDemo)
            return new(OrderStatus.Rejected,
                "MT5 execution requires a fresh, explicitly verified Demo account.");

        var position = GetOwnedPosition(positionId);
        if (position is null)
            return new(OrderStatus.Rejected,
                "MT5 position is missing or is not unambiguously owned by GoldAiTrader.");
        var command = new MT5ModifyPositionCommand(CreateEnvelope(), Guid.NewGuid(),
            account.AccountIdentifier, position.PositionId, position.SignalId!.Value,
            position.ClientCorrelationId!, position.StrategyVersion!, position.OwnershipTag!,
            stopLoss, takeProfit);
        return await SendAsync(availableTransport, command, cancellationToken);
    }

    private bool TryGetTransport(out IMT5LoopbackExecutionTransport availableTransport,
        out ExecutionResult failure)
    {
        if (IsTransportReady())
        {
            availableTransport = transport!;
            failure = null!;
            return true;
        }

        availableTransport = null!;
        failure = new(OrderStatus.Rejected,
            "MT5 execution transport is unavailable, unauthenticated, or lacks identity round-trip support; no order was sent.");
        return false;
    }

    private bool IsTransportReady() =>
        transport is not null &&
        transport.IsAvailable &&
        transport.IsAuthenticated &&
        transport.SupportsClientCorrelationIds &&
        transport.SupportsOwnershipMetadata;

    private PositionSnapshot? GetOwnedPosition(string positionId)
    {
        if (string.IsNullOrWhiteSpace(positionId))
            return null;
        var matches = state.GetTrustedPositions().Where(position =>
            string.Equals(position.PositionId, positionId, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 &&
            PositionOwnership.Classify(matches[0]) == PositionOwnershipStatus.Owned
                ? matches[0]
                : null;
    }

    private MT5MessageEnvelope CreateEnvelope() =>
        new(MT5BridgeProtocol.CurrentVersion, state.Identity.BridgeInstanceId,
            timeProvider.GetUtcNow().ToUniversalTime());

    private async Task<ExecutionResult> SendAsync(IMT5LoopbackExecutionTransport availableTransport,
        MT5ExecutionCommand command, CancellationToken cancellationToken)
    {
        var acknowledgement = await availableTransport.SendAsync(command, cancellationToken);
        MT5BridgeProtocol.ValidateEnvelope(acknowledgement.Envelope, state.Identity);
        if (acknowledgement.CommandId != command.CommandId)
            return new(OrderStatus.Indeterminate,
                "MT5 acknowledgement does not match the execution command.");
        return acknowledgement.Result;
    }
}

public sealed class MT5PlatformGateway : ITradingPlatformGateway
{
    private readonly MT5BridgeState state;
    private readonly TimeProvider timeProvider;

    public MT5PlatformGateway(MT5BridgeState state,
        IMT5LoopbackExecutionTransport? executionTransport = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.state = state;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        MarketDataProvider = new MT5MarketDataProvider(state);
        TradingAccountProvider = new MT5TradingAccountProvider(state);
        SymbolSpecificationProvider = new MT5SymbolSpecificationProvider(state);
        PositionProvider = new MT5PositionProvider(state);
        ExecutionGateway = new MT5ExecutionGateway(state, executionTransport, this.timeProvider);
        ExecutionMetadataSupport = executionTransport;

        var capabilities = PlatformCapabilities.StreamingMarketData |
            PlatformCapabilities.AccountInformation |
            PlatformCapabilities.AccountEnvironmentClassification |
            PlatformCapabilities.CanonicalSymbolMapping |
            PlatformCapabilities.SymbolSpecifications |
            PlatformCapabilities.PositionInventory;

        if (executionTransport is not null)
        {
            if (executionTransport.SupportsClientCorrelationIds)
                capabilities |= PlatformCapabilities.ClientCorrelationIds;
            if (executionTransport.SupportsOwnershipMetadata)
                capabilities |= PlatformCapabilities.OwnershipMetadata;
            if (executionTransport.SupportsClientCorrelationIds &&
                executionTransport.SupportsOwnershipMetadata)
                capabilities |= PlatformCapabilities.SubmitMarketOrders |
                    PlatformCapabilities.ClosePositions |
                    PlatformCapabilities.ModifyPositions |
                    PlatformCapabilities.StopLoss |
                    PlatformCapabilities.TakeProfit;
        }

        var canonicalSymbol = state.CanonicalSymbols.Count == 1
            ? state.CanonicalSymbols[0]
            : null;
        Descriptor = new(new PlatformIdentity("MetaTrader 5",
            "GoldAiTrader MT5 Bridge Adapter", state.Identity.AdapterVersion,
            state.Identity.BrokerName, state.Identity.BridgeInstanceId),
            capabilities, canonicalSymbol);
    }

    public PlatformDescriptor Descriptor { get; }
    public IMarketDataProvider? MarketDataProvider { get; }
    public IHistoricalMarketDataProvider? HistoricalMarketDataProvider => null;
    public ITradingAccountProvider? TradingAccountProvider { get; }
    public ISymbolSpecificationProvider? SymbolSpecificationProvider { get; }
    public IPositionProvider? PositionProvider { get; }
    public IExecutionGateway? ExecutionGateway { get; }
    public IExecutionMetadataSupport? ExecutionMetadataSupport { get; }

    public Task<PlatformStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = state.GetStatusSnapshot();
        return Task.FromResult(new PlatformStatus(Descriptor, snapshot.Health.Healthy,
            snapshot.Account?.AccountIdentifier ?? string.Empty,
            snapshot.Account?.Environment ?? AccountEnvironment.Unknown,
            timeProvider.GetUtcNow(), Descriptor.CanonicalSymbol, snapshot.Health.Reason));
    }
}
