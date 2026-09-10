using GoldAiTrader.Bot;
using GoldAiTrader.Core;
using GoldAiTrader.Data;
using GoldAiTrader.Execution;
using GoldAiTrader.Risk;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GoldAiTrader.Core.Tests;

public sealed class OperationalPersistenceTests
{
    [Fact]
    public async Task ConcurrentSignalReservationHasExactlyOneWinner()
    {
        var journal = new InMemoryExecutionJournal();
        var signalId = Guid.NewGuid();
        var attempts = Enumerable.Range(0, 20)
            .Select(_ => journal.TryReserveAsync(Record(signalId, ExecutionLifecycle.Reserved)));

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
        Assert.Single(await journal.GetAllAsync());
    }

    [Fact]
    public async Task ExecutorRestartPreservesAcceptedSignalIdempotency()
    {
        var journal = new InMemoryExecutionJournal();
        var signalId = Guid.NewGuid();
        var firstGateway = new FakeGateway();
        var first = Executor(firstGateway, journal, Ready());

        Assert.Equal(OrderStatus.Accepted, (await first.SubmitAsync(Order(signalId))).Status);

        var restartedGateway = new FakeGateway();
        var restarted = Executor(restartedGateway, journal, Ready());
        var duplicate = await restarted.SubmitAsync(Order(signalId));

        Assert.Equal(OrderStatus.Duplicate, duplicate.Status);
        Assert.Equal(0, restartedGateway.SubmitCalls);
        Assert.Equal(ExecutionLifecycle.Accepted, (await journal.GetAsync(signalId))!.State);
    }

    [Fact]
    public async Task IndeterminateOutcomeIsNeverResubmittedAfterRestart()
    {
        var journal = new InMemoryExecutionJournal();
        var signalId = Guid.NewGuid();
        var uncertainGateway = new FakeGateway { SubmitException = new TimeoutException("broker timeout") };

        var uncertain = await Executor(uncertainGateway, journal, Ready())
            .SubmitAsync(Order(signalId));
        var restartedGateway = new FakeGateway();
        var retried = await Executor(restartedGateway, journal, Ready())
            .SubmitAsync(Order(signalId));

        Assert.Equal(OrderStatus.Indeterminate, uncertain.Status);
        Assert.Equal(ExecutionLifecycle.Indeterminate, (await journal.GetAsync(signalId))!.State);

        Assert.Equal(OrderStatus.Duplicate, retried.Status);
        Assert.Equal(0, restartedGateway.SubmitCalls);
    }

    [Fact]
    public async Task CancellationAfterSubmissionIsPersistedAsIndeterminate()
    {
        var journal = new InMemoryExecutionJournal();
        var signalId = Guid.NewGuid();
        var gateway = new FakeGateway
        {
            SubmitException = new OperationCanceledException("cancelled after broker call")
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Executor(gateway, journal, Ready()).SubmitAsync(Order(signalId)));

        Assert.Equal(ExecutionLifecycle.Indeterminate, (await journal.GetAsync(signalId))!.State);
        Assert.Equal(1, gateway.SubmitCalls);
    }

    [Fact]
    public void PositionOwnershipDistinguishesOwnedManualAndAmbiguousPositions()
    {
        var signalId = Guid.NewGuid();
        var owned = OwnedPosition(signalId);
        var manual = owned with
        {
            SignalId = null, ClientCorrelationId = null, StrategyVersion = null, OwnershipTag = null
        };
        var ambiguous = owned with { ClientCorrelationId = null };

        Assert.Equal(PositionOwnershipStatus.Owned, PositionOwnership.Classify(owned));
        Assert.Equal(PositionOwnershipStatus.Unowned, PositionOwnership.Classify(manual));
        Assert.Equal(PositionOwnershipStatus.Ambiguous, PositionOwnership.Classify(ambiguous));
    }

    [Fact]
    public async Task OnlyOwnedPositionsCanBeClosedOrModified()
    {
        var signalId = Guid.NewGuid();
        var owned = OwnedPosition(signalId);
        var manual = owned with
        {
            PositionId = "manual", SignalId = null, ClientCorrelationId = null,
            StrategyVersion = null, OwnershipTag = null
        };
        var ambiguous = owned with { PositionId = "ambiguous", StrategyVersion = null };
        var gateway = new FakeGateway();
        var executor = Executor(gateway, new InMemoryExecutionJournal(),
            Ready(), new FakePositions([owned, manual, ambiguous]));

        Assert.Equal(OrderStatus.Accepted, (await executor.CloseAsync(owned.PositionId)).Status);
        Assert.Equal(OrderStatus.Rejected, (await executor.CloseAsync(manual.PositionId)).Status);
        Assert.Equal(OrderStatus.Rejected, (await executor.ModifyAsync(ambiguous.PositionId, 1999m, 2002m)).Status);
        Assert.Equal(1, gateway.CloseCalls);
        Assert.Equal(0, gateway.ModifyCalls);
    }

    [Fact]
    public async Task AcceptedExecutionAndMatchingOwnedPositionReconcileCleanly()
    {
        var signalId = Guid.NewGuid();
        var journal = await JournalWithAsync(Record(signalId, ExecutionLifecycle.Accepted));
        var report = await new ExecutionReconciliationService(journal,
            new FakePositions([OwnedPosition(signalId)])).ReconcileAsync("demo-account", Now);

        Assert.True(report.IsSafeForNewSubmissions);
        Assert.Contains(report.Items, item => item.Status == ReconciliationStatus.MatchedAccepted);
    }

    [Theory]
    [InlineData(ExecutionLifecycle.Indeterminate)]
    [InlineData(ExecutionLifecycle.Submitted)]
    public async Task MatchingPositionResolvesUnknownExecution(ExecutionLifecycle state)
    {
        var signalId = Guid.NewGuid();
        var journal = await JournalWithAsync(Record(signalId, state));
        var report = await new ExecutionReconciliationService(journal,
            new FakePositions([OwnedPosition(signalId)])).ReconcileAsync("demo-account", Now);

        Assert.True(report.IsSafeForNewSubmissions);
        Assert.Contains(report.Items, item => item.Status == ReconciliationStatus.IndeterminateReconciled);
        Assert.Equal(ExecutionLifecycle.Reconciled, (await journal.GetAsync(signalId))!.State);
    }

    [Theory]
    [InlineData(ExecutionLifecycle.Accepted, ReconciliationStatus.MissingBrokerPosition)]
    [InlineData(ExecutionLifecycle.Submitted, ReconciliationStatus.IndeterminateUnresolved)]
    [InlineData(ExecutionLifecycle.Indeterminate, ReconciliationStatus.IndeterminateUnresolved)]
    public async Task MissingExpectedPositionBlocksStartup(ExecutionLifecycle state,
        ReconciliationStatus expectedStatus)
    {
        var journal = await JournalWithAsync(Record(Guid.NewGuid(), state));
        var report = await new ExecutionReconciliationService(journal, new FakePositions([]))
            .ReconcileAsync("demo-account", Now);

        Assert.False(report.IsSafeForNewSubmissions);
        Assert.Contains(report.Items, item => item.Status == expectedStatus);
    }

    [Fact]
    public async Task UnexpectedOwnedPositionBlocksStartupWhileManualPositionDoesNot()
    {
        var signalId = Guid.NewGuid();
        var owned = OwnedPosition(signalId);
        var manual = owned with
        {
            PositionId = "manual", SignalId = null, ClientCorrelationId = null,
            StrategyVersion = null, OwnershipTag = null
        };
        var report = await new ExecutionReconciliationService(new InMemoryExecutionJournal(),
            new FakePositions([owned, manual])).ReconcileAsync("demo-account", Now);

        Assert.False(report.IsSafeForNewSubmissions);
        Assert.Contains(report.Items, item => item.Status == ReconciliationStatus.UnexpectedOwnedPosition);
        Assert.Contains(report.Items, item => item.Status == ReconciliationStatus.UnrelatedPosition);
    }

    [Fact]
    public async Task MismatchedOwnedMetadataIsAmbiguousAndBlocksStartup()
    {
        var signalId = Guid.NewGuid();
        var journal = await JournalWithAsync(Record(signalId, ExecutionLifecycle.Accepted));
        var position = OwnedPosition(signalId) with { StrategyVersion = "different-version" };

        var report = await new ExecutionReconciliationService(journal, new FakePositions([position]))
            .ReconcileAsync("demo-account", Now);

        Assert.False(report.IsSafeForNewSubmissions);
        Assert.Contains(report.Items, item => item.Status == ReconciliationStatus.AmbiguousOwnership);
    }

    [Fact]
    public async Task OwnedPositionContradictingRejectedJournalStateBlocksStartup()
    {
        var signalId = Guid.NewGuid();
        var journal = await JournalWithAsync(Record(signalId, ExecutionLifecycle.Rejected));

        var report = await new ExecutionReconciliationService(journal,
            new FakePositions([OwnedPosition(signalId)])).ReconcileAsync("demo-account", Now);

        Assert.False(report.IsSafeForNewSubmissions);
        Assert.Contains(report.Items, item => item.Status == ReconciliationStatus.UnexpectedOwnedPosition);
    }

    [Fact]
    public async Task FailedReconciliationGateBlocksNewDemoSubmission()
    {
        var journal = await JournalWithAsync(Record(Guid.NewGuid(), ExecutionLifecycle.Accepted));
        var report = await new ExecutionReconciliationService(journal, new FakePositions([]))
            .ReconcileAsync("demo-account", Now);
        var gate = new StartupReconciliationGate();
        gate.Apply(report);
        var gateway = new FakeGateway();

        var result = await Executor(gateway, journal, Ready(gate)).SubmitAsync(Order(Guid.NewGuid()));

        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Equal(0, gateway.SubmitCalls);
    }

    [Fact]
    public async Task SuccessfulStartupReconciliationAllowsNewDemoSubmission()
    {
        var existingSignal = Guid.NewGuid();
        var journal = await JournalWithAsync(Record(existingSignal, ExecutionLifecycle.Accepted));
        var report = await new ExecutionReconciliationService(journal,
            new FakePositions([OwnedPosition(existingSignal)])).ReconcileAsync("demo-account", Now);
        var gate = new StartupReconciliationGate();
        gate.Apply(report);
        var gateway = new FakeGateway();

        var result = await Executor(gateway, journal, Ready(gate)).SubmitAsync(Order(Guid.NewGuid()));

        Assert.True(report.IsSafeForNewSubmissions);
        Assert.Equal(OrderStatus.Accepted, result.Status);
        Assert.Equal(1, gateway.SubmitCalls);
    }

    [Fact]
    public async Task EfRiskStatePersistsAcrossContextRestart()
    {
        var options = InMemoryOptions();
        await using (var firstContext = new TradingDbContext(options))
            await new EfRiskStateRepository(firstContext).SaveAsync("demo-account", new(3, Now.AddHours(4)));

        await using var restartedContext = new TradingDbContext(options);
        var restored = await new EfRiskStateRepository(restartedContext).LoadAsync("demo-account");

        Assert.Equal(3, restored.ConsecutiveLosses);
        Assert.Equal(Now.AddHours(4), restored.SuspendedUntil);
    }

    [Fact]
    public async Task EmergencyShutdownPersistsAndRequiresExplicitReset()
    {
        var options = InMemoryOptions();
        await using (var firstContext = new TradingDbContext(options))
            await new EfOperationalSafetyStateRepository(firstContext)
                .SaveEmergencyShutdownAsync("demo-account", "operator stop", Now);

        await using (var restartedContext = new TradingDbContext(options))
        {
            var repository = new EfOperationalSafetyStateRepository(restartedContext);
            var guard = new OperationalGuard(new(ExecutionMode.Demo, EnableTrading: true), repository,
                "demo-account");
            guard.SetBrokerConnected(true);
            guard.RecordMarketData(Now);

            Assert.False(guard.GetStatus(Now).AcceptingNewTrades);
            Assert.Contains("not been restored", guard.GetStatus(Now).Reason);

            await guard.RestoreSafetyStateAsync();
            Assert.True(guard.GetStatus(Now).EmergencyShutdown);
            Assert.False(guard.GetStatus(Now).AcceptingNewTrades);

            await guard.ClearEmergencyShutdownAsync();
            Assert.False(guard.GetStatus(Now).EmergencyShutdown);
            Assert.True(guard.GetStatus(Now).AcceptingNewTrades);
        }

        await using var finalContext = new TradingDbContext(options);
        var persisted = await new EfOperationalSafetyStateRepository(finalContext).LoadAsync("demo-account");
        Assert.False(persisted.EmergencyShutdown);
        Assert.Equal("Explicit administrative reset.", persisted.Reason);
    }

    [Fact]
    public async Task EfExecutionJournalRoundTripsLifecycleAcrossContextRestart()
    {
        var options = InMemoryOptions();
        var signalId = Guid.NewGuid();
        await using (var firstContext = new TradingDbContext(options))
        {
            var journal = new EfExecutionJournal(firstContext);
            Assert.True(await journal.TryReserveAsync(Record(signalId, ExecutionLifecycle.Reserved)));
            await journal.UpdateAsync(signalId, ExecutionLifecycle.Accepted,
                new(OrderStatus.Accepted, "filled", "order-1", BrokerPositionId: "position-1"), Now);
        }

        await using var restartedContext = new TradingDbContext(options);
        var restored = await new EfExecutionJournal(restartedContext).GetAsync(signalId);

        Assert.NotNull(restored);
        Assert.Equal(ExecutionLifecycle.Accepted, restored.State);
        Assert.Equal("order-1", restored.BrokerOrderId);
        Assert.Equal("position-1", restored.BrokerPositionId);
        Assert.Equal(0, restored.SubmissionAttempts);
    }

    [Fact]
    public void EfModelHasDurableIdentityConstraintsAndDiscoverableMigration()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=goldaitrader_model_test;Username=postgres")
            .Options;
        using var context = new TradingDbContext(options);
        var execution = context.Model.FindEntityType(typeof(ExecutionJournalEntity))!;
        var uniqueProperties = execution.GetIndexes().Where(index => index.IsUnique)
            .Select(index => string.Join(",", index.Properties.Select(property => property.Name)))
            .ToArray();

        Assert.Contains(nameof(ExecutionJournalEntity.SignalId), uniqueProperties);
        Assert.Contains(nameof(ExecutionJournalEntity.ClientCorrelationId), uniqueProperties);
        Assert.Contains(nameof(ExecutionJournalEntity.BrokerPositionId), uniqueProperties);
        Assert.Contains(context.Database.GetMigrations(),
            migration => migration.EndsWith("_InitialOperationalSchema", StringComparison.Ordinal));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-26T00:00:00Z");

    private static ExecutionJournalRecord Record(Guid signalId, ExecutionLifecycle state) =>
        new(signalId, $"GoldAiTrader-{signalId:N}", "XAUUSD", "demo-account", "v1",
            TradeDirection.Buy, 2.5m, 2000m, 1999m, 2002m, state, "order-1", null,
            Now.AddMinutes(-1), Now.AddMinutes(-1));

    private static PositionSnapshot OwnedPosition(Guid signalId) =>
        new($"position-{signalId:N}", "XAUUSD", "GOLD", TradeDirection.Buy, 2.5m,
            2000m, 1999m, 2002m, 2.5m, signalId, $"GoldAiTrader-{signalId:N}", "v1",
            PositionOwnership.DefaultOwnershipTag);

    private static ApprovedOrder Order(Guid signalId) =>
        new(new(signalId, Now, "XAUUSD", TradeDirection.Buy, 2000m, 1999m, 2002m,
            "v1", "test"), new(2.5m, 2.5m));

    private static GatewayTradeExecutor Executor(FakeGateway gateway, IExecutionJournal journal,
        ExternalExecutionReadiness readiness, IPositionProvider? positions = null) =>
        new(new(ExecutionMode.Demo, TradingEnabled: true), gateway, journal, readiness,
            positions ?? new FakePositions([]));

    private static ExternalExecutionReadiness Ready(string accountIdentifier = "demo-account")
    {
        var gate = new StartupReconciliationGate();
        gate.Apply(new(accountIdentifier, [], Now));
        return Ready(gate);
    }

    private static ExternalExecutionReadiness Ready(StartupReconciliationGate gate,
        bool operationallyReady = true) => new(gate, new TestOperationalReadiness(operationallyReady));

    private static async Task<InMemoryExecutionJournal> JournalWithAsync(ExecutionJournalRecord record)
    {
        var journal = new InMemoryExecutionJournal();
        Assert.True(await journal.TryReserveAsync(record));
        return journal;
    }

    private static DbContextOptions<TradingDbContext> InMemoryOptions()
    {
        var root = new InMemoryDatabaseRoot();
        return new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"goldaitrader-{Guid.NewGuid():N}", root).Options;
    }

    private sealed class TestOperationalReadiness(bool ready) : IOperationalExecutionReadiness
    {
        public Task<ExecutionReadiness> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionReadiness(ready, ready ? "Ready." : "Operationally blocked."));
    }

    private sealed class FakePositions(IReadOnlyList<PositionSnapshot> positions) : IPositionProvider
    {
        public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(positions);
    }

    private sealed class FakeGateway : IExecutionGateway
    {
        public string ProviderName => "fake";
        public int SubmitCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int ModifyCalls { get; private set; }
        public Exception? SubmitException { get; init; }

        public Task<ExecutionAccountState> GetExecutionAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutionAccountState("demo-account", AccountEnvironment.Demo, true));

        public Task<ExecutionResult> SubmitAsync(ApprovedOrder order, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            if (SubmitException is not null) throw SubmitException;
            return Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "filled", "order-new",
                BrokerPositionId: "position-new"));
        }

        public Task<ExecutionResult> CloseAsync(string positionId, CancellationToken cancellationToken)
        {
            CloseCalls++;
            return Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "closed"));
        }

        public Task<ExecutionResult> ModifyAsync(string positionId, decimal stopLoss, decimal takeProfit,
            CancellationToken cancellationToken)
        {
            ModifyCalls++;
            return Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "modified"));
        }
    }
}
