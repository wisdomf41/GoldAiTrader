using GoldAiTrader.Adapters.CTrader;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Platform;

namespace GoldAiTrader.Core.Tests;

// Updated: universal gateway tests now verify explicit historical-data and execution-metadata contracts.
public sealed class UniversalPlatformGatewayTests
{
    private static readonly PlatformCapabilities DemoCapabilities =
        PlatformCapabilityValidator.RequiredFor(ExecutionMode.Demo);

    [Fact]
    public void FullyCapablePlatformPassesDemoValidation()
    {
        var platform = Gateway("replaceable-platform", DemoCapabilities);

        var result = PlatformCapabilityValidator.Validate(
            platform,
            ExecutionMode.Demo);

        Assert.True(result.IsSuitable);
        Assert.Empty(result.MissingCapabilities);
    }

    [Fact]
    public void MissingMarketDataCapabilityIsReported()
    {
        var result = ValidateDemoWithout(
            PlatformCapabilities.StreamingMarketData);

        AssertMissing(
            result,
            PlatformCapabilities.StreamingMarketData);
    }

    [Fact]
    public void MissingAccountClassificationCapabilityIsReported()
    {
        var result = ValidateDemoWithout(
            PlatformCapabilities.AccountEnvironmentClassification);

        AssertMissing(
            result,
            PlatformCapabilities.AccountEnvironmentClassification);
    }

    [Fact]
    public void MissingSymbolSpecificationCapabilityIsReported()
    {
        var result = ValidateDemoWithout(
            PlatformCapabilities.SymbolSpecifications);

        AssertMissing(
            result,
            PlatformCapabilities.SymbolSpecifications);
    }

    [Fact]
    public void MissingPositionInventoryCapabilityBlocksExecutableSuitability()
    {
        var result = ValidateDemoWithout(
            PlatformCapabilities.PositionInventory);

        AssertMissing(
            result,
            PlatformCapabilities.PositionInventory);
    }

    [Fact]
    public void MissingOrderSubmissionCapabilityBlocksDemoSuitability()
    {
        var result = ValidateDemoWithout(
            PlatformCapabilities.SubmitMarketOrders);

        AssertMissing(
            result,
            PlatformCapabilities.SubmitMarketOrders);
    }

    [Fact]
    public void HistoricalBarsCapabilityRequiresHistoricalProviderContract()
    {
        var capabilities =
            PlatformCapabilityValidator.RequiredFor(ExecutionMode.Backtest);

        var platform = Gateway(
            "historical-flag-only",
            capabilities,
            includeHistoricalData: false,
            includeAccount: false,
            includePositions: false,
            includeExecution: false);

        var result = PlatformCapabilityValidator.Validate(
            platform,
            ExecutionMode.Backtest);

        AssertMissing(
            result,
            PlatformCapabilities.HistoricalBars);
    }

    [Fact]
    public void ClientCorrelationCapabilityRequiresMetadataSupport()
    {
        var platform = Gateway(
            "metadata-platform",
            DemoCapabilities,
            supportsClientCorrelationIds: false);

        var result = PlatformCapabilityValidator.Validate(
            platform,
            ExecutionMode.Demo);

        AssertMissing(
            result,
            PlatformCapabilities.ClientCorrelationIds);
    }

    [Fact]
    public void OwnershipCapabilityRequiresMetadataSupport()
    {
        var platform = Gateway(
            "metadata-platform",
            DemoCapabilities,
            supportsOwnershipMetadata: false);

        var result = PlatformCapabilityValidator.Validate(
            platform,
            ExecutionMode.Demo);

        AssertMissing(
            result,
            PlatformCapabilities.OwnershipMetadata);
    }

    [Fact]
    public void BacktestDoesNotRequireBrokerExecutionCapabilities()
    {
        var capabilities =
            PlatformCapabilityValidator.RequiredFor(ExecutionMode.Backtest);

        var platform = Gateway(
            "file-replay",
            capabilities,
            includeAccount: false,
            includePositions: false,
            includeExecution: false);

        var result = PlatformCapabilityValidator.Validate(
            platform,
            ExecutionMode.Backtest);

        Assert.True(result.IsSuitable);

        Assert.False(
            result.RequiredCapabilities.HasFlag(
                PlatformCapabilities.SubmitMarketOrders));

        Assert.Null(platform.ExecutionGateway);
    }

    [Fact]
    public void ShadowDoesNotRequireOrderSubmissionCapabilities()
    {
        var capabilities =
            PlatformCapabilityValidator.RequiredFor(ExecutionMode.Shadow);

        var platform = Gateway(
            "market-data-link",
            capabilities,
            includePositions: false,
            includeExecution: false);

        var result = PlatformCapabilityValidator.Validate(
            platform,
            ExecutionMode.Shadow);

        Assert.True(result.IsSuitable);

        Assert.False(
            result.RequiredCapabilities.HasFlag(
                PlatformCapabilities.SubmitMarketOrders));
    }

    [Fact]
    public void NewPlatformNamesRequireNoUniversalCodeChanges()
    {
        const string platformName = "Future Quantum Broker Link";

        var platform = Gateway(
            platformName,
            DemoCapabilities);

        var result = PlatformCapabilityValidator.Validate(
            platform,
            ExecutionMode.Demo);

        Assert.True(result.IsSuitable);

        Assert.Equal(
            platformName,
            platform.Descriptor.Identity.PlatformName);
    }

    [Fact]
    public void SymbolNormalizationRemainsAdapterSpecific()
    {
        var mapper = new CTraderSymbolMapper(
            new Dictionary<string, string>
            {
                ["XAUUSD"] = "METAL.SPOT-7"
            });

        var source = new CTraderSymbolDescription(
            "METAL.SPOT-7",
            0.01m,
            1m,
            100m,
            0.01m,
            100m,
            0.01m,
            0.1m,
            true);

        var normalized = mapper.Normalize(
            "XAUUSD",
            source);

        Assert.Equal(
            "XAUUSD",
            normalized.CanonicalSymbol);

        Assert.Equal(
            "METAL.SPOT-7",
            normalized.BrokerSymbol);
    }

    [Fact]
    public async Task CurrentCTraderBoundaryImplementsUniversalContract()
    {
        ITradingPlatformGateway platform =
            new CTraderPlatformGateway(
                new FakeCTraderClient());

        var status = await platform.GetStatusAsync();

        Assert.Equal(
            "cTrader",
            platform.Descriptor.Identity.PlatformName);

        Assert.NotNull(
            platform.TradingAccountProvider);

        Assert.NotNull(
            platform.ExecutionGateway);

        Assert.Equal(
            AccountEnvironment.Demo,
            status.AccountEnvironment);

        Assert.True(status.Connected);
    }

    [Fact]
    public void LiveModeRemainsBlockedEvenWithEveryCapability()
    {
        var all = Enum.GetValues<PlatformCapabilities>()
            .Aggregate(
                PlatformCapabilities.None,
                (current, capability) =>
                    current | capability);

        var platform = Gateway(
            "fully-capable",
            all);

        var result = PlatformCapabilityValidator.Validate(
            platform,
            ExecutionMode.Live);

        Assert.False(result.IsSuitable);

        Assert.Contains(
            result.BlockingReasons,
            reason =>
                reason.Contains(
                    "Live",
                    StringComparison.Ordinal));
    }

    private static PlatformCapabilityValidation ValidateDemoWithout(
        PlatformCapabilities capability) =>
        PlatformCapabilityValidator.Validate(
            Gateway(
                "test-platform",
                DemoCapabilities & ~capability),
            ExecutionMode.Demo);

    private static void AssertMissing(
        PlatformCapabilityValidation result,
        PlatformCapabilities capability)
    {
        Assert.False(result.IsSuitable);

        Assert.Contains(
            capability,
            result.MissingCapabilities);

        Assert.Contains(
            capability.ToString(),
            result.Summary,
            StringComparison.Ordinal);
    }

    private static FakePlatformGateway Gateway(
        string platformName,
        PlatformCapabilities capabilities,
        bool includeMarketData = true,
        bool includeHistoricalData = true,
        bool includeAccount = true,
        bool includeSymbol = true,
        bool includePositions = true,
        bool includeExecution = true,
        bool includeExecutionMetadata = true,
        bool supportsClientCorrelationIds = true,
        bool supportsOwnershipMetadata = true) =>
        new(
            new PlatformDescriptor(
                new PlatformIdentity(
                    platformName,
                    $"{platformName} adapter",
                    "1.0.0",
                    "test broker",
                    "test-connection"),
                capabilities,
                "XAUUSD"),

            includeMarketData
                ? new FakeMarketDataProvider()
                : null,

            includeHistoricalData
                ? new FakeHistoricalMarketDataProvider()
                : null,

            includeAccount
                ? new FakeAccountProvider()
                : null,

            includeSymbol
                ? new FakeSymbolProvider()
                : null,

            includePositions
                ? new FakePositionProvider()
                : null,

            includeExecution
                ? new FakeExecutionGateway()
                : null,

            includeExecutionMetadata
                ? new FakeExecutionMetadataSupport(
                    supportsClientCorrelationIds,
                    supportsOwnershipMetadata)
                : null);

    private sealed class FakePlatformGateway(
        PlatformDescriptor descriptor,
        IMarketDataProvider? marketDataProvider,
        IHistoricalMarketDataProvider? historicalMarketDataProvider,
        ITradingAccountProvider? tradingAccountProvider,
        ISymbolSpecificationProvider? symbolSpecificationProvider,
        IPositionProvider? positionProvider,
        IExecutionGateway? executionGateway,
        IExecutionMetadataSupport? executionMetadataSupport)
        : ITradingPlatformGateway
    {
        public PlatformDescriptor Descriptor { get; } =
            descriptor;

        public IMarketDataProvider? MarketDataProvider { get; } =
            marketDataProvider;

        public IHistoricalMarketDataProvider?
            HistoricalMarketDataProvider { get; } =
                historicalMarketDataProvider;

        public ITradingAccountProvider?
            TradingAccountProvider { get; } =
                tradingAccountProvider;

        public ISymbolSpecificationProvider?
            SymbolSpecificationProvider { get; } =
                symbolSpecificationProvider;

        public IPositionProvider? PositionProvider { get; } =
            positionProvider;

        public IExecutionGateway? ExecutionGateway { get; } =
            executionGateway;

        public IExecutionMetadataSupport?
            ExecutionMetadataSupport { get; } =
                executionMetadataSupport;

        public Task<PlatformStatus> GetStatusAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new PlatformStatus(
                    Descriptor,
                    true,
                    "demo-account",
                    AccountEnvironment.Demo,
                    DateTimeOffset.UtcNow,
                    Descriptor.CanonicalSymbol));
    }

    private sealed class FakeMarketDataProvider :
        IMarketDataProvider
    {
        public async IAsyncEnumerable<MarketBar> StreamBarsAsync(
            string canonicalSymbol,
            TimeSpan timeframe,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.CompletedTask;

            yield break;
        }
    }

    private sealed class FakeHistoricalMarketDataProvider :
        IHistoricalMarketDataProvider
    {
        public Task<IReadOnlyList<MarketBar>> GetBarsAsync(
            string canonicalSymbol,
            TimeSpan timeframe,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<IReadOnlyList<MarketBar>>(
                []);
        }
    }

    private sealed class FakeExecutionMetadataSupport(
        bool supportsClientCorrelationIds,
        bool supportsOwnershipMetadata)
        : IExecutionMetadataSupport
    {
        public bool SupportsClientCorrelationIds { get; } =
            supportsClientCorrelationIds;

        public bool SupportsOwnershipMetadata { get; } =
            supportsOwnershipMetadata;
    }

    private sealed class FakeAccountProvider :
        ITradingAccountProvider
    {
        public Task<AccountSnapshot> GetAccountAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new AccountSnapshot(
                    1000m,
                    1000m,
                    1000m,
                    1000m,
                    1000m,
                    1000m,
                    0,
                    0m,
                    AccountIdentifier: "demo-account",
                    Environment: AccountEnvironment.Demo));
    }

    private sealed class FakeSymbolProvider :
        ISymbolSpecificationProvider
    {
        public Task<SymbolSpecification> GetSpecificationAsync(
            string canonicalSymbol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new SymbolSpecification(
                    0.01m,
                    1m,
                    0.01m,
                    100m,
                    0.01m,
                    0.1m,
                    "broker-gold",
                    canonicalSymbol,
                    100m,
                    true));
    }

    private sealed class FakePositionProvider :
        IPositionProvider
    {
        public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PositionSnapshot>>(
                []);
    }

    private sealed class FakeExecutionGateway :
        IExecutionGateway
    {
        public string ProviderName => "fake";

        public Task<ExecutionAccountState> GetExecutionAccountAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ExecutionAccountState(
                    "demo-account",
                    AccountEnvironment.Demo,
                    true));

        public Task<ExecutionResult> SubmitAsync(
            ApprovedOrder order,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ExecutionResult(
                    OrderStatus.Accepted,
                    "accepted"));

        public Task<ExecutionResult> CloseAsync(
            string positionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ExecutionResult(
                    OrderStatus.Accepted,
                    "closed"));

        public Task<ExecutionResult> ModifyAsync(
            string positionId,
            decimal stopLoss,
            decimal takeProfit,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ExecutionResult(
                    OrderStatus.Accepted,
                    "modified"));
    }

    private sealed class FakeCTraderClient :
        ICTraderPlatformClient
    {
        public Task<AccountSnapshot> GetAccountAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new AccountSnapshot(
                    1000m,
                    1000m,
                    1000m,
                    1000m,
                    1000m,
                    1000m,
                    0,
                    0m,
                    AccountIdentifier: "ctrader-demo",
                    Environment: AccountEnvironment.Demo));

        public Task<bool> IsConnectedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<ExecutionResult> SubmitDemoOrderAsync(
            ApprovedOrder order,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ExecutionResult(
                    OrderStatus.Accepted,
                    "accepted"));

        public Task<ExecutionResult> CloseDemoPositionAsync(
            string positionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ExecutionResult(
                    OrderStatus.Accepted,
                    "closed"));

        public Task<ExecutionResult> ModifyDemoPositionAsync(
            string positionId,
            decimal stopLoss,
            decimal takeProfit,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ExecutionResult(
                    OrderStatus.Accepted,
                    "modified"));
    }
}