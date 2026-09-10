using GoldAiTrader.Core;
using GoldAiTrader.Execution;

namespace GoldAiTrader.Platform;

// Updated: Universal platform gateway now requires explicit contracts for historical data and execution metadata capabilities.
[Flags]
public enum PlatformCapabilities : ulong
{
    None = 0,
    StreamingMarketData = 1UL << 0,
    HistoricalBars = 1UL << 1,
    AccountInformation = 1UL << 2,
    AccountEnvironmentClassification = 1UL << 3,
    CanonicalSymbolMapping = 1UL << 4,
    SymbolSpecifications = 1UL << 5,
    PositionInventory = 1UL << 6,
    SubmitMarketOrders = 1UL << 7,
    ClosePositions = 1UL << 8,
    ModifyPositions = 1UL << 9,
    StopLoss = 1UL << 10,
    TakeProfit = 1UL << 11,
    ClientCorrelationIds = 1UL << 12,
    OwnershipMetadata = 1UL << 13,
    ReconnectSupport = 1UL << 14
}

public sealed record PlatformIdentity
{
    public PlatformIdentity(
        string platformName,
        string adapterName,
        string adapterVersion,
        string? brokerName = null,
        string? connectionIdentifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformName);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterVersion);

        PlatformName = platformName;
        AdapterName = adapterName;
        AdapterVersion = adapterVersion;
        BrokerName = NormalizeOptional(brokerName);
        ConnectionIdentifier = NormalizeOptional(connectionIdentifier);
    }

    public string PlatformName { get; }
    public string AdapterName { get; }
    public string AdapterVersion { get; }
    public string? BrokerName { get; }
    public string? ConnectionIdentifier { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record PlatformDescriptor(
    PlatformIdentity Identity,
    PlatformCapabilities Capabilities,
    string? CanonicalSymbol = null);

public sealed record PlatformStatus(
    PlatformDescriptor Descriptor,
    bool? Connected,
    string AccountIdentifier,
    AccountEnvironment AccountEnvironment,
    DateTimeOffset ObservedAt,
    string? CanonicalSymbol = null,
    string? Detail = null);

public interface IHistoricalMarketDataProvider
{
    Task<IReadOnlyList<MarketBar>> GetBarsAsync(
        string canonicalSymbol,
        TimeSpan timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
}

public interface IExecutionMetadataSupport
{
    bool SupportsClientCorrelationIds { get; }
    bool SupportsOwnershipMetadata { get; }
}

public interface ITradingPlatformGateway
{
    PlatformDescriptor Descriptor { get; }

    IMarketDataProvider? MarketDataProvider { get; }

    IHistoricalMarketDataProvider? HistoricalMarketDataProvider { get; }

    ITradingAccountProvider? TradingAccountProvider { get; }

    ISymbolSpecificationProvider? SymbolSpecificationProvider { get; }

    IPositionProvider? PositionProvider { get; }

    IExecutionGateway? ExecutionGateway { get; }

    IExecutionMetadataSupport? ExecutionMetadataSupport { get; }

    Task<PlatformStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);
}

public sealed record PlatformCapabilityValidation(
    ExecutionMode Mode,
    PlatformCapabilities RequiredCapabilities,
    IReadOnlyList<PlatformCapabilities> MissingCapabilities,
    IReadOnlyList<string> BlockingReasons)
{
    public bool IsSuitable =>
        MissingCapabilities.Count == 0 &&
        BlockingReasons.Count == 0;

    public string Summary =>
        IsSuitable
            ? $"Platform adapter satisfies {Mode} capability requirements."
            : string.Join(" ", BlockingReasons);
}

public static class PlatformCapabilityValidator
{
    private static readonly PlatformCapabilities BacktestRequirements =
        PlatformCapabilities.HistoricalBars |
        PlatformCapabilities.CanonicalSymbolMapping;

    private static readonly PlatformCapabilities ShadowRequirements =
        PlatformCapabilities.StreamingMarketData |
        PlatformCapabilities.AccountInformation |
        PlatformCapabilities.CanonicalSymbolMapping |
        PlatformCapabilities.SymbolSpecifications;

    private static readonly PlatformCapabilities DemoRequirements =
        ShadowRequirements |
        PlatformCapabilities.AccountEnvironmentClassification |
        PlatformCapabilities.PositionInventory |
        PlatformCapabilities.SubmitMarketOrders |
        PlatformCapabilities.ClosePositions |
        PlatformCapabilities.ModifyPositions |
        PlatformCapabilities.StopLoss |
        PlatformCapabilities.TakeProfit |
        PlatformCapabilities.ClientCorrelationIds |
        PlatformCapabilities.OwnershipMetadata;

    public static PlatformCapabilities RequiredFor(ExecutionMode mode) =>
        mode switch
        {
            ExecutionMode.Backtest => BacktestRequirements,
            ExecutionMode.Shadow => ShadowRequirements,
            ExecutionMode.Demo => DemoRequirements,
            ExecutionMode.Live => DemoRequirements,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "Unknown execution mode.")
        };

    public static PlatformCapabilityValidation Validate(
        ITradingPlatformGateway gateway,
        ExecutionMode mode)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var required = RequiredFor(mode);
        var missing = new List<PlatformCapabilities>();
        var reasons = new List<string>();

        foreach (var capability in Enumerate(required))
        {
            if ((gateway.Descriptor.Capabilities & capability) != capability)
            {
                missing.Add(capability);
                reasons.Add(
                    $"Missing required platform capability: {capability}.");

                continue;
            }

            if (!HasSupportingContract(gateway, capability))
            {
                missing.Add(capability);
                reasons.Add(
                    $"Platform capability {capability} has no corresponding provider contract.");
            }
        }

        if (mode == ExecutionMode.Live)
        {
            reasons.Add(
                "Live platform execution is disabled by GoldAiTrader policy.");
        }

        return new(
            mode,
            required,
            missing,
            reasons);
    }

    private static IEnumerable<PlatformCapabilities> Enumerate(
        PlatformCapabilities capabilities) =>
        Enum.GetValues<PlatformCapabilities>()
            .Where(value =>
                value != PlatformCapabilities.None &&
                (capabilities & value) == value);

    private static bool HasSupportingContract(
        ITradingPlatformGateway gateway,
        PlatformCapabilities capability) =>
        capability switch
        {
            PlatformCapabilities.StreamingMarketData =>
                gateway.MarketDataProvider is not null,

            PlatformCapabilities.HistoricalBars =>
                gateway.HistoricalMarketDataProvider is not null,

            PlatformCapabilities.AccountInformation =>
                gateway.TradingAccountProvider is not null,

            PlatformCapabilities.AccountEnvironmentClassification =>
                gateway.TradingAccountProvider is not null ||
                gateway.ExecutionGateway is not null,

            PlatformCapabilities.CanonicalSymbolMapping or
            PlatformCapabilities.SymbolSpecifications =>
                gateway.SymbolSpecificationProvider is not null,

            PlatformCapabilities.PositionInventory =>
                gateway.PositionProvider is not null,

            PlatformCapabilities.SubmitMarketOrders or
            PlatformCapabilities.ClosePositions or
            PlatformCapabilities.ModifyPositions or
            PlatformCapabilities.StopLoss or
            PlatformCapabilities.TakeProfit =>
                gateway.ExecutionGateway is not null,

            PlatformCapabilities.ClientCorrelationIds =>
                gateway.ExecutionMetadataSupport?
                    .SupportsClientCorrelationIds == true,

            PlatformCapabilities.OwnershipMetadata =>
                gateway.ExecutionMetadataSupport?
                    .SupportsOwnershipMetadata == true,

            _ => true
        };
}