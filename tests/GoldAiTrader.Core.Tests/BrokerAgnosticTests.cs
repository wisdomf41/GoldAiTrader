using GoldAiTrader.Adapters.CTrader;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Risk;
using GoldAiTrader.Strategy;

namespace GoldAiTrader.Core.Tests;

public sealed class BrokerAgnosticTests
{
    [Fact]
    public void CoreHasNoPlatformOrBrokerAssemblyDependency()
    {
        var references = typeof(MarketBar).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain(references, name => name is not null &&
            (name.Contains("cTrader", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("cAlgo", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("MetaTrader", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("XAUUSD")]
    [InlineData("GOLD")]
    [InlineData("XAUUSD.a")]
    [InlineData("XAUUSDm")]
    public void CanonicalGoldMapsToDifferentBrokerSymbols(string brokerSymbol)
    {
        var mapper = new CTraderSymbolMapper(new Dictionary<string, string> { ["XAUUSD"] = brokerSymbol });
        var normalized = mapper.Normalize("XAUUSD", new(brokerSymbol, 0.01m, 0.01m, 100m, 1m, 100m, 1m, 0.1m, true));

        Assert.Equal("XAUUSD", normalized.CanonicalSymbol);
        Assert.Equal(brokerSymbol, normalized.BrokerSymbol);
    }

    [Fact]
    public void PositionSizingUsesDynamicSymbolSpecification()
    {
        var engine = new RiskEngine(new());
        var account = new AccountSnapshot(1000m, 1000m, 1000m, 1000m, 1000m, 1000m, 0, 0m);
        var setup = new TradeSetup(Guid.NewGuid(), DateTimeOffset.UtcNow, "XAUUSD", TradeDirection.Buy,
            2000m, 1999m, 2002m, "test", "test");
        var specificationA = new SymbolSpecification(0.1m, 0.1m, 1m, 100m, 1m, 0.1m,
            "GOLD", "XAUUSD", 100m, true);
        var specificationB = specificationA with { BrokerSymbol = "XAUUSDm", TickValuePerVolumeUnit = 0.2m };

        var sizeA = engine.Assess(setup, account, specificationA, 0.1m).PositionSize!;
        var sizeB = engine.Assess(setup, account, specificationB, 0.1m).PositionSize!;

        Assert.Equal(5m, sizeA.Volume);
        Assert.Equal(2m, sizeB.Volume);
        Assert.True(sizeB.PlannedRiskAmount <= 5m);
    }

    [Fact]
    public void RiskRejectsCanonicalSymbolMismatch()
    {
        var setup = new TradeSetup(Guid.NewGuid(), DateTimeOffset.UtcNow, "XAUUSD", TradeDirection.Buy,
            2000m, 1999m, 2002m, "test", "test");
        var account = new AccountSnapshot(1000m, 1000m, 1000m, 1000m, 1000m, 1000m, 0, 0m);
        var specification = new SymbolSpecification(0.1m, 0.1m, 1m, 100m, 1m, 0.1m,
            "SILVER", "XAGUSD", 100m, true);

        Assert.False(new RiskEngine(new()).Assess(setup, account, specification, 0.1m).Approved);
    }

    [Fact]
    public async Task BrokerNeutralExecutorAcceptsReplaceableGateway()
    {
        var gateway = new TestGateway();
        var executor = new GatewayTradeExecutor(new(ExecutionMode.Demo, TradingEnabled: true), gateway,
            new InMemoryExecutionJournal(), TestExecutionReadiness.Ready());
        var setup = new TradeSetup(Guid.NewGuid(), DateTimeOffset.UtcNow, "XAUUSD", TradeDirection.Buy,
            2000m, 1999m, 2002m, "test", "test");

        var result = await executor.SubmitAsync(new(setup, new(1m, 1m)));

        Assert.Equal(OrderStatus.Accepted, result.Status);
        Assert.Equal(1, gateway.Submissions);
    }

    [Fact]
    public void StrategyConsumesCanonicalNormalizedFeatures()
    {
        var features = new MarketFeatures(DateTimeOffset.Parse("2026-01-05T14:00:00Z"), "XAUUSD",
            2000m, 2000.1m, 2000m, 1999m, 2001m, 1990m, 2m, 60m, 30m, 0.01m,
            MarketSession.LondonNewYorkOverlap, MarketRegime.Trending);

        Assert.Equal(StrategyAction.Buy, new TrendPullbackV1(new()).Evaluate(features, new(0, false, null)).Action);
    }

    private sealed class TestGateway : IExecutionGateway
    {
        public string ProviderName => "test";
        public int Submissions { get; private set; }
        public Task<ExecutionAccountState> GetExecutionAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutionAccountState("demo-account", AccountEnvironment.Demo, true));
        public Task<ExecutionResult> SubmitAsync(ApprovedOrder order, CancellationToken cancellationToken)
        {
            Submissions++;
            return Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "accepted"));
        }
        public Task<ExecutionResult> CloseAsync(string positionId, CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "closed"));
        public Task<ExecutionResult> ModifyAsync(string positionId, decimal stopLoss, decimal takeProfit,
            CancellationToken cancellationToken) => Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "modified"));
    }
}
