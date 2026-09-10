using GoldAiTrader.Adapters.CTrader;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;

namespace GoldAiTrader.Core.Tests;

public sealed class ExecutionSafetyTests
{
    [Fact]
    public void UnpopulatedAccountDefaultsToUnknownNotDemo()
    {
        var account = AccountSnapshot();
        Assert.Equal(AccountEnvironment.Unknown, account.Environment);
        Assert.False(account.IsExplicitDemo);
    }

    [Fact]
    public async Task ExplicitVerifiedDemoAccountCanSubmit()
    {
        var gateway = new FakeGateway(State("demo-123", AccountEnvironment.Demo, true));
        var result = await Executor(gateway).SubmitAsync(Order());
        Assert.Equal(OrderStatus.Accepted, result.Status);
        Assert.Equal(1, gateway.SubmitCalls);
    }

    [Theory]
    [InlineData(AccountEnvironment.Live, "demo-123", true)]
    [InlineData(AccountEnvironment.Unknown, "demo-123", true)]
    [InlineData(AccountEnvironment.Demo, "", true)]
    [InlineData(AccountEnvironment.Demo, "demo-123", false)]
    public async Task UnsafeAccountStatesFailClosed(AccountEnvironment environment, string identifier, bool connected)
    {
        var gateway = new FakeGateway(State(identifier, environment, connected));
        var result = await Executor(gateway).SubmitAsync(Order());
        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Fact]
    public async Task TradingMustBeExplicitlyEnabled()
    {
        var gateway = new FakeGateway(State("demo-123", AccountEnvironment.Demo, true));
        var executor = new GatewayTradeExecutor(new(ExecutionMode.Demo), gateway);
        Assert.Equal(OrderStatus.Rejected, (await executor.SubmitAsync(Order())).Status);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Fact]
    public async Task CloseAndModifyAreBlockedForUnsafeAccount()
    {
        var gateway = new FakeGateway(State("live-123", AccountEnvironment.Live, true));
        var executor = Executor(gateway);
        Assert.Equal(OrderStatus.Rejected, (await executor.CloseAsync("position-1")).Status);
        Assert.Equal(OrderStatus.Rejected, (await executor.ModifyAsync("position-1", 1999m, 2002m)).Status);
        Assert.Equal(0, gateway.CloseCalls);
        Assert.Equal(0, gateway.ModifyCalls);
    }

    [Fact]
    public async Task CloseAndModifyRequireVerifiedDemoAndReachGateway()
    {
        var gateway = new FakeGateway(State("demo-123", AccountEnvironment.Demo, true));
        var executor = Executor(gateway);
        Assert.Equal(OrderStatus.Accepted, (await executor.CloseAsync("position-1")).Status);
        Assert.Equal(OrderStatus.Accepted, (await executor.ModifyAsync("position-1", 1999m, 2002m)).Status);
        Assert.Equal(1, gateway.CloseCalls);
        Assert.Equal(1, gateway.ModifyCalls);
    }

    [Fact]
    public async Task OnlyProvablySafeRejectionIsRetried()
    {
        var gateway = new FakeGateway(State("demo-123", AccountEnvironment.Demo, true));
        gateway.Results.Enqueue(new(OrderStatus.Rejected, "not submitted", RetryDisposition: RetryDisposition.SafeToRetry));
        gateway.Results.Enqueue(new(OrderStatus.Accepted, "filled", "order-1"));
        var result = await Executor(gateway).SubmitAsync(Order());
        Assert.Equal(OrderStatus.Accepted, result.Status);
        Assert.Equal(2, gateway.SubmitCalls);
    }

    [Theory]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Duplicate)]
    [InlineData(OrderStatus.Indeterminate)]
    public async Task NonRetryableOrUnknownOutcomeIsNeverRetried(OrderStatus status)
    {
        var gateway = new FakeGateway(State("demo-123", AccountEnvironment.Demo, true));
        gateway.Results.Enqueue(new(status, "stop"));
        gateway.Results.Enqueue(new(OrderStatus.Accepted, "must not execute"));
        Assert.Equal(status, (await Executor(gateway).SubmitAsync(Order())).Status);
        Assert.Equal(1, gateway.SubmitCalls);
    }

    [Fact]
    public async Task GatewayExceptionBecomesIndeterminateWithoutRetry()
    {
        var gateway = new FakeGateway(State("demo-123", AccountEnvironment.Demo, true)) { ThrowOnSubmit = true };
        var result = await Executor(gateway).SubmitAsync(Order());
        Assert.Equal(OrderStatus.Indeterminate, result.Status);
        Assert.Equal(1, gateway.SubmitCalls);
    }

    [Fact]
    public async Task ExcessivePostFillSlippagePreservesAcceptedFill()
    {
        var gateway = new FakeGateway(State("demo-123", AccountEnvironment.Demo, true));
        gateway.Results.Enqueue(new(OrderStatus.Accepted, "filled", "order-42", 0.75m));
        var executor = new GatewayTradeExecutor(new(ExecutionMode.Demo, MaximumSlippage: 0.50m, TradingEnabled: true), gateway,
            new InMemoryExecutionJournal(), TestExecutionReadiness.Ready("demo-123"));
        var result = await executor.SubmitAsync(Order());
        Assert.Equal(OrderStatus.Accepted, result.Status);
        Assert.Equal("order-42", result.BrokerOrderId);
        Assert.Equal(ExecutionSafetyStatus.SlippageLimitExceeded, result.SafetyStatus);
    }

    [Fact]
    public async Task CTraderGatewayUsesNormalizedAccountMetadata()
    {
        var client = new FakeCTraderClient(AccountSnapshot("ctrader-demo", AccountEnvironment.Demo), true);
        var state = await new CTraderExecutionGateway(client).GetExecutionAccountAsync(default);
        Assert.Equal("ctrader-demo", state.AccountIdentifier);
        Assert.Equal(AccountEnvironment.Demo, state.Environment);
        Assert.True(state.BrokerConnected);
    }

    private static GatewayTradeExecutor Executor(FakeGateway gateway) =>
        new(new(ExecutionMode.Demo, MaximumRetries: 2, TradingEnabled: true), gateway,
            new InMemoryExecutionJournal(), TestExecutionReadiness.Ready("demo-123"),
            new FakePositionProvider([OwnedPosition()]));

    private static ApprovedOrder Order() => new(
        new TradeSetup(Guid.NewGuid(), DateTimeOffset.UtcNow, "XAUUSD", TradeDirection.Buy,
            2000m, 1999m, 2002m, "test", "test"), new PositionSize(1m, 1m));

    private static ExecutionAccountState State(string id, AccountEnvironment environment, bool connected) =>
        new(id, environment, connected);

    private static AccountSnapshot AccountSnapshot(string id = "", AccountEnvironment environment = AccountEnvironment.Unknown) =>
        new(1000m, 1000m, 1000m, 1000m, 1000m, 1000m, 0, 0m,
            AccountIdentifier: id, Environment: environment);
    private static PositionSnapshot OwnedPosition() => new("position-1", "XAUUSD", "GOLD",
        TradeDirection.Buy, 1m, 2000m, 1999m, 2002m, 1m, Guid.NewGuid(),
        "GoldAiTrader-test", "test", PositionOwnership.DefaultOwnershipTag);

    private sealed class FakePositionProvider(IReadOnlyList<PositionSnapshot> positions) : IPositionProvider
    { public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(positions); }

    private sealed class FakeGateway(ExecutionAccountState state) : IExecutionGateway
    {
        public string ProviderName => "fake";
        public Queue<ExecutionResult> Results { get; } = new();
        public int SubmitCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int ModifyCalls { get; private set; }
        public bool ThrowOnSubmit { get; init; }
        public Task<ExecutionAccountState> GetExecutionAccountAsync(CancellationToken cancellationToken) => Task.FromResult(state);
        public Task<ExecutionResult> SubmitAsync(ApprovedOrder order, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            if (ThrowOnSubmit) throw new TimeoutException("unknown broker outcome");
            return Task.FromResult(Results.TryDequeue(out var result) ? result : new(OrderStatus.Accepted, "accepted"));
        }
        public Task<ExecutionResult> CloseAsync(string positionId, CancellationToken cancellationToken)
        {
            CloseCalls++;
            return Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "closed"));
        }
        public Task<ExecutionResult> ModifyAsync(string positionId, decimal stopLoss, decimal takeProfit, CancellationToken cancellationToken)
        {
            ModifyCalls++;
            return Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "modified"));
        }
    }

    private sealed class FakeCTraderClient(AccountSnapshot account, bool connected) : ICTraderPlatformClient
    {
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken cancellationToken) => Task.FromResult(account);
        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(connected);
        public Task<ExecutionResult> SubmitDemoOrderAsync(ApprovedOrder order, CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "accepted"));
        public Task<ExecutionResult> CloseDemoPositionAsync(string positionId, CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "closed"));
        public Task<ExecutionResult> ModifyDemoPositionAsync(string positionId, decimal stopLoss, decimal takeProfit,
            CancellationToken cancellationToken) => Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "modified"));
    }
}
