using GoldAiTrader.Bot;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Platform;
using GoldAiTrader.Risk;

namespace GoldAiTrader.Core.Tests;

public sealed class OperationalStartupIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");

    [Fact]
    public async Task ReconciliationUsesOnlyTheVerifiedAccountJournal()
    {
        var journal = new InMemoryExecutionJournal();
        var signalA = Guid.NewGuid();
        var signalB = Guid.NewGuid();
        Assert.True(await journal.TryReserveAsync(Record(signalA, "account-a",
            ExecutionLifecycle.Accepted)));
        Assert.True(await journal.TryReserveAsync(Record(signalB, "account-b",
            ExecutionLifecycle.Accepted)));

        var report = await new ExecutionReconciliationService(journal,
            new FakePositions([OwnedPosition(signalB)])).ReconcileAsync("account-b", Now);

        Assert.True(report.IsSafeForNewSubmissions);
        Assert.Equal("account-b", report.AccountIdentifier);
        Assert.Single(report.Items);
        Assert.Equal(signalB, report.Items[0].SignalId);
        Assert.Equal(ReconciliationStatus.MatchedAccepted, report.Items[0].Status);
    }

    [Fact]
    public async Task ReconciliationRequiresAccountIdentifier()
    {
        var service = new ExecutionReconciliationService(new InMemoryExecutionJournal(),
            new FakePositions([]));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReconcileAsync("", Now));
    }

    [Fact]
    public async Task KnownBrokerPositionIdMismatchBlocksReadiness()
    {
        var journal = new InMemoryExecutionJournal();
        var signal = Guid.NewGuid();
        Assert.True(await journal.TryReserveAsync(Record(signal, "demo-account",
            ExecutionLifecycle.Accepted, "expected-position")));

        var report = await new ExecutionReconciliationService(journal,
            new FakePositions([OwnedPosition(signal) with { PositionId = "different-position" }]))
            .ReconcileAsync("demo-account", Now);

        Assert.False(report.IsSafeForNewSubmissions);
        Assert.Contains(report.Items, item => item.Status == ReconciliationStatus.AmbiguousOwnership &&
            item.Reason.Contains("position ID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmergencyShutdownBlocksExternalDemoSubmission()
    {
        var guard = HealthyGuard();
        guard.TriggerEmergencyShutdown();
        var gateway = new FakeGateway();

        var result = await Executor(gateway, Readiness(guard)).SubmitAsync(Order());

        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Contains("Emergency shutdown", result.Reason);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Fact]
    public async Task UnrestoredDurableSafetyStateBlocksExternalDemoSubmission()
    {
        var guard = HealthyGuard(new MemorySafetyRepository(), restore: false);
        var gateway = new FakeGateway();

        var result = await Executor(gateway, Readiness(guard)).SubmitAsync(Order());

        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Contains("not been restored", result.Reason);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Fact]
    public async Task StaleMarketDataBlocksExternalDemoSubmission()
    {
        var guard = new OperationalGuard(new(ExecutionMode.Demo, EnableTrading: true),
            timeProvider: new FixedTimeProvider(Now));
        guard.SetBrokerConnected(true);
        guard.RecordMarketData(Now.AddHours(-1));
        var gateway = new FakeGateway();

        var result = await Executor(gateway, Readiness(guard)).SubmitAsync(Order());

        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Contains("stale", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Fact]
    public async Task UnresolvedReconciliationBlocksExternalDemoSubmission()
    {
        var gate = new StartupReconciliationGate();
        gate.Apply(new("demo-account",
            [new(ReconciliationStatus.MissingBrokerPosition, Guid.NewGuid(), null, "missing")], Now));
        var gateway = new FakeGateway();

        var result = await Executor(gateway,
            new(gate, HealthyGuard())).SubmitAsync(Order());

        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Contains("unresolved", result.Reason);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Fact]
    public async Task HealthyOperationalAndReconciliationReadinessAllowsDemoSubmission()
    {
        var gateway = new FakeGateway();

        var result = await Executor(gateway, Readiness(HealthyGuard())).SubmitAsync(Order());

        Assert.Equal(OrderStatus.Accepted, result.Status);
        Assert.Equal(1, gateway.SubmitCalls);
    }

    [Fact]
    public void ProductionAssemblyExposesNoAlwaysReadyBypass() =>
        Assert.Null(typeof(ExternalExecutionReadiness).Assembly
            .GetType("GoldAiTrader.Execution.AlwaysReadyExecutionGate"));

    [Fact]
    public void RiskAssessmentFailsClosedBeforePersistedStateRestoration()
    {
        var session = new PersistedRiskSession(new(), new MemoryRiskRepository(), "demo-account");

        var assessment = session.AssessExecutable(Setup(), Account(), Symbol(), 0.1m, Now);

        Assert.False(assessment.Approved);
        Assert.Contains("not been restored", assessment.Reason);
    }

    [Fact]
    public async Task PersistedConsecutiveLossSuspensionSurvivesRestartAndReachesRiskEngine()
    {
        var repository = new MemoryRiskRepository
        {
            State = new RiskState(3, Now.AddHours(2))
        };
        var restarted = new PersistedRiskSession(new(), repository, "demo-account");

        await restarted.RestoreAsync();
        var assessment = restarted.AssessExecutable(Setup(), Account(), Symbol(), 0.1m, Now);

        Assert.True(restarted.IsRestored);
        Assert.Equal(repository.State, restarted.State);
        Assert.False(assessment.Approved);
        Assert.Contains("cooldown", assessment.Reason);
    }

    [Fact]
    public async Task ClosedTradeUpdatesAndPersistsLossAndWinningReset()
    {
        var repository = new MemoryRiskRepository();
        var session = new PersistedRiskSession(new(), repository, "demo-account");
        await session.RestoreAsync();

        var loss = await session.RecordClosedTradeAsync(-1m, Now);
        Assert.Equal(1, loss.ConsecutiveLosses);
        Assert.Equal(loss, repository.State);

        var reset = await session.RecordClosedTradeAsync(0m, Now.AddMinutes(1));
        Assert.Equal(new RiskState(), reset);
        Assert.Equal(reset, repository.State);
        Assert.Equal(2, repository.SaveCalls);
    }

    [Fact]
    public async Task StartupCoordinatorRestoresStateReconcilesAccountAndOpensReadiness()
    {
        var safety = new MemorySafetyRepository();
        var riskRepository = new MemoryRiskRepository();
        var risk = new PersistedRiskSession(new(), riskRepository, "demo-account");
        var guard = HealthyGuard(safety, restore: false);
        var gateway = new FakeGateway();
        var journal = new InMemoryExecutionJournal();
        var gate = new StartupReconciliationGate();
        var platform = new FakePlatformGateway(gateway, new FakePositions([]));
        var coordinator = new DemoStartupCoordinator(guard, risk, platform,
            journal, gate);

        var result = await coordinator.StartAsync(Now);
        var submission = await Executor(gateway,
            new ExternalExecutionReadiness(gate, guard), journal).SubmitAsync(Order());

        Assert.True(result.Ready);
        Assert.True(risk.IsRestored);
        Assert.Equal("demo-account", result.Reconciliation!.AccountIdentifier);
        Assert.Equal(OrderStatus.Accepted, submission.Status);
    }

    [Fact]
    public async Task FailedStartupAttemptInvalidatesPreviouslySafeReconciliation()
    {
        var guard = HealthyGuard();
        var risk = new PersistedRiskSession(new(), new MemoryRiskRepository(), "demo-account");
        var gateway = new FakeGateway
        {
            AccountState = new("live-account", AccountEnvironment.Live, true)
        };
        var gate = new StartupReconciliationGate();
        gate.Apply(new("demo-account", [], Now));
        var journal = new InMemoryExecutionJournal();
        var platform = new FakePlatformGateway(gateway, new FakePositions([]));
        var coordinator = new DemoStartupCoordinator(guard, risk, platform, journal, gate);

        var result = await coordinator.StartAsync(Now);
        var readiness = await gate.CheckAsync("demo-account");

        Assert.False(result.Ready);
        Assert.False(readiness.Ready);
        Assert.Contains("not completed", readiness.Reason);
    }

    [Fact]
    public async Task MissingPositionInventoryCapabilityBlocksDemoStartup()
    {
        var guard = HealthyGuard();
        var risk = new PersistedRiskSession(new(), new MemoryRiskRepository(), "demo-account");
        var gateway = new FakeGateway();
        var gate = new StartupReconciliationGate();
        var capabilities = PlatformCapabilityValidator.RequiredFor(ExecutionMode.Demo) &
            ~PlatformCapabilities.PositionInventory;
        var platform = new FakePlatformGateway(gateway, null, capabilities);
        var coordinator = new DemoStartupCoordinator(guard, risk, platform,
            new InMemoryExecutionJournal(), gate);

        var result = await coordinator.StartAsync(Now);
        var readiness = await gate.CheckAsync("demo-account");

        Assert.False(result.Ready);
        Assert.Contains(nameof(PlatformCapabilities.PositionInventory), result.Reason);
        Assert.False(readiness.Ready);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    private static OperationalGuard HealthyGuard(MemorySafetyRepository? repository = null,
        bool restore = true)
    {
        var guard = new OperationalGuard(new(ExecutionMode.Demo, EnableTrading: true), repository,
            "demo-account", new FixedTimeProvider(Now));
        guard.SetBrokerConnected(true);
        guard.RecordMarketData(Now);
        if (repository is not null && restore)
            guard.RestoreSafetyStateAsync().GetAwaiter().GetResult();
        return guard;
    }

    private static ExternalExecutionReadiness Readiness(IOperationalExecutionReadiness operational)
    {
        var gate = new StartupReconciliationGate();
        gate.Apply(new("demo-account", [], Now));
        return new(gate, operational);
    }

    private static GatewayTradeExecutor Executor(FakeGateway gateway,
        ExternalExecutionReadiness readiness, IExecutionJournal? journal = null) =>
        new(new(ExecutionMode.Demo, TradingEnabled: true), gateway,
            journal ?? new InMemoryExecutionJournal(), readiness);

    private static ExecutionJournalRecord Record(Guid signalId, string account,
        ExecutionLifecycle state, string? brokerPositionId = null) =>
        new(signalId, $"GoldAiTrader-{signalId:N}", "XAUUSD", account, "v1",
            TradeDirection.Buy, 2.5m, 2000m, 1999m, 2002m, state, "order-1",
            brokerPositionId, Now, Now);

    private static PositionSnapshot OwnedPosition(Guid signalId) =>
        new($"position-{signalId:N}", "XAUUSD", "GOLD", TradeDirection.Buy, 2.5m,
            2000m, 1999m, 2002m, 2.5m, signalId, $"GoldAiTrader-{signalId:N}", "v1",
            PositionOwnership.DefaultOwnershipTag);

    private static ApprovedOrder Order() => new(Setup(), new(2.5m, 2.5m));

    private static TradeSetup Setup() =>
        new(Guid.NewGuid(), Now, "XAUUSD", TradeDirection.Buy, 2000m, 1999m, 2002m,
            "v1", "test");

    private static AccountSnapshot Account() =>
        new(1000m, 1000m, 1000m, 1000m, 1000m, 1000m, 0, 0m);

    private static SymbolSpecification Symbol() =>
        new(0.1m, 0.1m, 1m, 100m, 1m, 0.1m, "GOLD", "XAUUSD", 100m, true);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryRiskRepository : IRiskStateRepository
    {
        public RiskState State { get; set; } = new();
        public int SaveCalls { get; private set; }
        public Task<RiskState> LoadAsync(string scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(State);
        public Task SaveAsync(string scope, RiskState state,
            CancellationToken cancellationToken = default)
        {
            State = state;
            SaveCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySafetyRepository : IOperationalSafetyStateRepository
    {
        public OperationalSafetyState State { get; set; } =
            new("demo-account", false, null, null);
        public Task<OperationalSafetyState> LoadAsync(string scope,
            CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task SaveEmergencyShutdownAsync(string scope, string reason, DateTimeOffset activatedAt,
            CancellationToken cancellationToken = default)
        {
            State = new(scope, true, activatedAt, reason);
            return Task.CompletedTask;
        }
        public Task ClearEmergencyShutdownAsync(string scope,
            CancellationToken cancellationToken = default)
        {
            State = new(scope, false, null, "reset");
            return Task.CompletedTask;
        }
    }

private sealed class FakePlatformGateway : ITradingPlatformGateway
{
    private readonly FakeGateway execution;

    public FakePlatformGateway(
        FakeGateway execution,
        IPositionProvider? positionProvider,
        PlatformCapabilities? capabilities = null)
    {
        this.execution = execution;

        Descriptor = new(
            new(
                "test-platform",
                "test-adapter",
                "1.0.0"),
            capabilities ??
                PlatformCapabilityValidator.RequiredFor(
                    ExecutionMode.Demo),
            "XAUUSD");

        MarketDataProvider =
            new FakeMarketDataProvider();

        HistoricalMarketDataProvider = null;

        TradingAccountProvider =
            new FakeAccountProvider(
                execution.AccountState);

        SymbolSpecificationProvider =
            new FakeSymbolSpecificationProvider();

        PositionProvider =
            positionProvider;

        ExecutionGateway =
            execution;

        ExecutionMetadataSupport =
            new FakeExecutionMetadataSupport();
    }

    public PlatformDescriptor Descriptor { get; }

    public IMarketDataProvider? MarketDataProvider { get; }

    public IHistoricalMarketDataProvider?
        HistoricalMarketDataProvider { get; }

    public ITradingAccountProvider?
        TradingAccountProvider { get; }

    public ISymbolSpecificationProvider?
        SymbolSpecificationProvider { get; }

    public IPositionProvider?
        PositionProvider { get; }

    public IExecutionGateway?
        ExecutionGateway { get; }

    public IExecutionMetadataSupport?
        ExecutionMetadataSupport { get; }

    public Task<PlatformStatus> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            new PlatformStatus(
                Descriptor,
                execution.AccountState.BrokerConnected,
                execution.AccountState.AccountIdentifier,
                execution.AccountState.Environment,
                Now,
                "XAUUSD"));
}

private sealed class FakeExecutionMetadataSupport :
    IExecutionMetadataSupport
{
    public bool SupportsClientCorrelationIds =>
        true;

    public bool SupportsOwnershipMetadata =>
        true;
}

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<MarketBar> StreamBarsAsync(string canonicalSymbol,
            TimeSpan timeframe,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeAccountProvider(ExecutionAccountState state) : ITradingAccountProvider
    {
        public Task<AccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account() with
            {
                AccountIdentifier = state.AccountIdentifier,
                Environment = state.Environment
            });
    }

    private sealed class FakeSymbolSpecificationProvider : ISymbolSpecificationProvider
    {
        public Task<SymbolSpecification> GetSpecificationAsync(string canonicalSymbol,
            CancellationToken cancellationToken = default) => Task.FromResult(Symbol());
    }

    private sealed class FakePositions(IReadOnlyList<PositionSnapshot> positions) : IPositionProvider
    {
        public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(positions);
    }

    private sealed class FakeGateway : IExecutionGateway
    {
        public string ProviderName => "fake";
        public ExecutionAccountState AccountState { get; init; } =
            new("demo-account", AccountEnvironment.Demo, true);
        public int SubmitCalls { get; private set; }
        public Task<ExecutionAccountState> GetExecutionAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(AccountState);
        public Task<ExecutionResult> SubmitAsync(ApprovedOrder order, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            return Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "accepted", "order-new"));
        }
        public Task<ExecutionResult> CloseAsync(string positionId, CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "closed"));
        public Task<ExecutionResult> ModifyAsync(string positionId, decimal stopLoss, decimal takeProfit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "modified"));
    }
}
