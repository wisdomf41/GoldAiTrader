using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Platform;

namespace GoldAiTrader.Adapters.CTrader;

// Updated: cTrader boundary now implements the explicit universal capability contracts.
public sealed record CTraderSymbolDescription(
    string Name,
    decimal TickSize,
    decimal TickValuePerVolumeUnit,
    decimal ContractSize,
    decimal MinimumVolume,
    decimal MaximumVolume,
    decimal VolumeStep,
    decimal MinimumStopDistance,
    bool IsTradingAvailable);

public sealed class CTraderSymbolMapper(
    IReadOnlyDictionary<string, string> brokerSymbols)
{
    public string GetBrokerSymbol(string canonicalSymbol) =>
        brokerSymbols.TryGetValue(canonicalSymbol, out var brokerSymbol)
            ? brokerSymbol
            : throw new KeyNotFoundException(
                $"No cTrader symbol mapping exists for canonical symbol '{canonicalSymbol}'.");

    public SymbolSpecification Normalize(
        string canonicalSymbol,
        CTraderSymbolDescription source)
    {
        var expectedBrokerSymbol = GetBrokerSymbol(canonicalSymbol);

        if (!string.Equals(
                expectedBrokerSymbol,
                source.Name,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The platform symbol does not match the configured canonical mapping.");
        }

        return new(
            source.TickSize,
            source.TickValuePerVolumeUnit,
            source.MinimumVolume,
            source.MaximumVolume,
            source.VolumeStep,
            source.MinimumStopDistance,
            source.Name,
            canonicalSymbol,
            source.ContractSize,
            source.IsTradingAvailable);
    }
}

public interface ICTraderPlatformClient : ITradingAccountProvider
{
    Task<bool> IsConnectedAsync(
        CancellationToken cancellationToken);

    Task<ExecutionResult> SubmitDemoOrderAsync(
        ApprovedOrder order,
        CancellationToken cancellationToken);

    Task<ExecutionResult> CloseDemoPositionAsync(
        string positionId,
        CancellationToken cancellationToken);

    Task<ExecutionResult> ModifyDemoPositionAsync(
        string positionId,
        decimal stopLoss,
        decimal takeProfit,
        CancellationToken cancellationToken);
}

public sealed class CTraderExecutionGateway(
    ICTraderPlatformClient client) : IExecutionGateway
{
    public string ProviderName => "cTrader";

    public async Task<ExecutionAccountState> GetExecutionAccountAsync(
        CancellationToken cancellationToken)
    {
        var account = await client.GetAccountAsync(cancellationToken);
        var connected = await client.IsConnectedAsync(cancellationToken);

        return new(
            account.AccountIdentifier,
            account.Environment,
            connected);
    }

    public Task<ExecutionResult> SubmitAsync(
        ApprovedOrder order,
        CancellationToken cancellationToken) =>
        client.SubmitDemoOrderAsync(order, cancellationToken);

    public Task<ExecutionResult> CloseAsync(
        string positionId,
        CancellationToken cancellationToken) =>
        client.CloseDemoPositionAsync(positionId, cancellationToken);

    public Task<ExecutionResult> ModifyAsync(
        string positionId,
        decimal stopLoss,
        decimal takeProfit,
        CancellationToken cancellationToken) =>
        client.ModifyDemoPositionAsync(
            positionId,
            stopLoss,
            takeProfit,
            cancellationToken);
}

public sealed record CTraderAdapterFeatures(
    bool HistoricalBars = false,
    bool ClientCorrelationIds = false,
    bool OwnershipMetadata = false,
    bool ReconnectSupport = false);

public sealed record CTraderExecutionMetadataSupport(
    bool SupportsClientCorrelationIds,
    bool SupportsOwnershipMetadata) : IExecutionMetadataSupport;

public sealed class CTraderPlatformGateway : ITradingPlatformGateway
{
    private readonly CTraderExecutionGateway executionGateway;

    public CTraderPlatformGateway(
        ICTraderPlatformClient client,
        IMarketDataProvider? marketDataProvider = null,
        IHistoricalMarketDataProvider? historicalMarketDataProvider = null,
        ISymbolSpecificationProvider? symbolSpecificationProvider = null,
        IPositionProvider? positionProvider = null,
        CTraderAdapterFeatures? features = null,
        string? brokerName = null,
        string? connectionIdentifier = null,
        string? canonicalSymbol = null,
        string adapterVersion = "1.0.0")
    {
        ArgumentNullException.ThrowIfNull(client);

        features ??= new();

        executionGateway = new(client);

        MarketDataProvider = marketDataProvider;
        HistoricalMarketDataProvider = historicalMarketDataProvider;
        TradingAccountProvider = client;
        SymbolSpecificationProvider = symbolSpecificationProvider;
        PositionProvider = positionProvider;
        ExecutionGateway = executionGateway;

        ExecutionMetadataSupport = new CTraderExecutionMetadataSupport(
            features.ClientCorrelationIds,
            features.OwnershipMetadata);

        var capabilities =
            PlatformCapabilities.AccountInformation |
            PlatformCapabilities.AccountEnvironmentClassification |
            PlatformCapabilities.SubmitMarketOrders |
            PlatformCapabilities.ClosePositions |
            PlatformCapabilities.ModifyPositions |
            PlatformCapabilities.StopLoss |
            PlatformCapabilities.TakeProfit;

        if (marketDataProvider is not null)
        {
            capabilities |=
                PlatformCapabilities.StreamingMarketData;
        }

        if (historicalMarketDataProvider is not null &&
            features.HistoricalBars)
        {
            capabilities |=
                PlatformCapabilities.HistoricalBars;
        }

        if (symbolSpecificationProvider is not null)
        {
            capabilities |=
                PlatformCapabilities.CanonicalSymbolMapping |
                PlatformCapabilities.SymbolSpecifications;
        }

        if (positionProvider is not null)
        {
            capabilities |=
                PlatformCapabilities.PositionInventory;
        }

        if (features.ClientCorrelationIds)
        {
            capabilities |=
                PlatformCapabilities.ClientCorrelationIds;
        }

        if (features.OwnershipMetadata)
        {
            capabilities |=
                PlatformCapabilities.OwnershipMetadata;
        }

        if (features.ReconnectSupport)
        {
            capabilities |=
                PlatformCapabilities.ReconnectSupport;
        }

        Descriptor = new(
            new(
                "cTrader",
                "GoldAiTrader cTrader Adapter",
                adapterVersion,
                brokerName,
                connectionIdentifier),
            capabilities,
            canonicalSymbol);
    }

    public PlatformDescriptor Descriptor { get; }

    public IMarketDataProvider? MarketDataProvider { get; }

    public IHistoricalMarketDataProvider? HistoricalMarketDataProvider { get; }

    public ITradingAccountProvider? TradingAccountProvider { get; }

    public ISymbolSpecificationProvider? SymbolSpecificationProvider { get; }

    public IPositionProvider? PositionProvider { get; }

    public IExecutionGateway? ExecutionGateway { get; }

    public IExecutionMetadataSupport? ExecutionMetadataSupport { get; }

    public async Task<PlatformStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var account =
            await executionGateway.GetExecutionAccountAsync(
                cancellationToken);

        return new(
            Descriptor,
            account.BrokerConnected,
            account.AccountIdentifier,
            account.Environment,
            DateTimeOffset.UtcNow,
            Descriptor.CanonicalSymbol);
    }
}