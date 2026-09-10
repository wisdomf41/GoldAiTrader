using System.Collections.Concurrent;
using GoldAiTrader.Core;

namespace GoldAiTrader.Execution;

public enum ExecutionLifecycle { Reserved, Submitted, Accepted, Rejected, Indeterminate, Reconciled }

public sealed record ExecutionJournalRecord(Guid SignalId, string ClientCorrelationId, string CanonicalSymbol,
    string AccountIdentifier, string StrategyVersion, TradeDirection Direction, decimal RequestedVolume,
    decimal RequestedEntry, decimal RequestedStopLoss, decimal RequestedTakeProfit,
    ExecutionLifecycle State, string? BrokerOrderId, string? BrokerPositionId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int SubmissionAttempts = 0,
    string? LastMessage = null);

public interface IExecutionJournal
{
    Task<bool> TryReserveAsync(ExecutionJournalRecord record, CancellationToken cancellationToken = default);
    Task<ExecutionJournalRecord?> GetAsync(Guid signalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionJournalRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionJournalRecord>> GetByAccountAsync(string accountIdentifier,
        CancellationToken cancellationToken = default);
    Task UpdateAsync(Guid signalId, ExecutionLifecycle state, ExecutionResult? result,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
}

public sealed class InMemoryExecutionJournal : IExecutionJournal
{
    private readonly ConcurrentDictionary<Guid, ExecutionJournalRecord> records = new();
    private readonly ConcurrentDictionary<string, Guid> correlations = new(StringComparer.Ordinal);

    public Task<bool> TryReserveAsync(ExecutionJournalRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!correlations.TryAdd(record.ClientCorrelationId, record.SignalId)) return Task.FromResult(false);
        if (records.TryAdd(record.SignalId, record)) return Task.FromResult(true);
        correlations.TryRemove(record.ClientCorrelationId, out _);
        return Task.FromResult(false);
    }

    public Task<ExecutionJournalRecord?> GetAsync(Guid signalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(records.TryGetValue(signalId, out var record) ? record : null);

    public Task<IReadOnlyList<ExecutionJournalRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExecutionJournalRecord>>(records.Values.OrderBy(x => x.CreatedAt).ToArray());

    public Task<IReadOnlyList<ExecutionJournalRecord>> GetByAccountAsync(string accountIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentifier);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ExecutionJournalRecord>>(records.Values
            .Where(x => string.Equals(x.AccountIdentifier, accountIdentifier, StringComparison.Ordinal))
            .OrderBy(x => x.CreatedAt).ToArray());
    }

    public Task UpdateAsync(Guid signalId, ExecutionLifecycle state, ExecutionResult? result,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        records.AddOrUpdate(signalId, _ => throw new InvalidOperationException("Signal was not reserved."), (_, current) =>
            current with { State = state, BrokerOrderId = result?.BrokerOrderId ?? current.BrokerOrderId,
                BrokerPositionId = result?.BrokerPositionId ?? current.BrokerPositionId,
                UpdatedAt = updatedAt, SubmissionAttempts = state == ExecutionLifecycle.Submitted
                    ? current.SubmissionAttempts + 1 : current.SubmissionAttempts,
                LastMessage = result?.Reason ?? current.LastMessage });
        return Task.CompletedTask;
    }
}

public enum PositionOwnershipStatus { Owned, Unowned, Ambiguous }

public static class PositionOwnership
{
    public const string DefaultOwnershipTag = "GoldAiTrader";

    public static PositionOwnershipStatus Classify(PositionSnapshot position, string ownershipTag = DefaultOwnershipTag)
    {
        var expectedTag = string.Equals(position.OwnershipTag, ownershipTag, StringComparison.Ordinal);
        var hasIdentity = position.SignalId.HasValue && !string.IsNullOrWhiteSpace(position.ClientCorrelationId) &&
            !string.IsNullOrWhiteSpace(position.StrategyVersion);
        if (expectedTag && hasIdentity) return PositionOwnershipStatus.Owned;
        if (string.IsNullOrWhiteSpace(position.OwnershipTag) && !position.SignalId.HasValue &&
            string.IsNullOrWhiteSpace(position.ClientCorrelationId)) return PositionOwnershipStatus.Unowned;
        if (!expectedTag && !hasIdentity) return PositionOwnershipStatus.Unowned;
        return PositionOwnershipStatus.Ambiguous;
    }
}

public enum ReconciliationStatus
{
    MatchedAccepted, IndeterminateReconciled, IndeterminateUnresolved, MissingBrokerPosition,
    UnexpectedOwnedPosition, UnrelatedPosition, AmbiguousOwnership, ConflictingOwnedPositions
}

public sealed record ReconciliationItem(ReconciliationStatus Status, Guid? SignalId, string? PositionId, string Reason)
{
    public bool BlocksSubmissions => Status is ReconciliationStatus.IndeterminateUnresolved or
        ReconciliationStatus.MissingBrokerPosition or ReconciliationStatus.UnexpectedOwnedPosition or
        ReconciliationStatus.AmbiguousOwnership or ReconciliationStatus.ConflictingOwnedPositions;
}

public sealed record ReconciliationReport(string AccountIdentifier, IReadOnlyList<ReconciliationItem> Items,
    DateTimeOffset CompletedAt)
{
    public bool IsSafeForNewSubmissions => Items.All(item => !item.BlocksSubmissions);
}

public sealed class ExecutionReconciliationService(IExecutionJournal journal, IPositionProvider positions)
{
    public async Task<ReconciliationReport> ReconcileAsync(string accountIdentifier, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentifier);
        var records = await journal.GetByAccountAsync(accountIdentifier, cancellationToken);
        var brokerPositions = await positions.GetPositionsAsync(cancellationToken);
        var items = new List<ReconciliationItem>();
        var owned = brokerPositions.Where(p => PositionOwnership.Classify(p) == PositionOwnershipStatus.Owned).ToArray();

        foreach (var position in brokerPositions)
        {
            var ownership = PositionOwnership.Classify(position);
            if (ownership == PositionOwnershipStatus.Unowned)
                items.Add(new(ReconciliationStatus.UnrelatedPosition, null, position.PositionId, "Position is not owned by GoldAiTrader."));
            else if (ownership == PositionOwnershipStatus.Ambiguous)
                items.Add(new(ReconciliationStatus.AmbiguousOwnership, position.SignalId, position.PositionId, "Position ownership is ambiguous."));
        }

        foreach (var record in records.Where(r => RequiresBrokerPosition(r.State)))
        {
            var signalMatches = owned.Where(p => p.SignalId == record.SignalId).ToArray();
            if (signalMatches.Length > 1)
            {
                items.Add(new(ReconciliationStatus.ConflictingOwnedPositions, record.SignalId, null, "Multiple owned positions match one signal."));
                continue;
            }
            if (signalMatches.Length == 0)
            {
                items.Add(new(record.State is ExecutionLifecycle.Submitted or ExecutionLifecycle.Indeterminate
                    ? ReconciliationStatus.IndeterminateUnresolved :
                    ReconciliationStatus.MissingBrokerPosition, record.SignalId, null, "Expected broker position is missing."));
                continue;
            }

            var match = signalMatches[0];
            if (record.BrokerPositionId is not null &&
                !string.Equals(match.PositionId, record.BrokerPositionId, StringComparison.Ordinal))
            {
                items.Add(new(ReconciliationStatus.AmbiguousOwnership, record.SignalId, match.PositionId,
                    "Broker position ID disagrees with the durable execution record."));
                continue;
            }
            if (!string.Equals(match.ClientCorrelationId, record.ClientCorrelationId, StringComparison.Ordinal) ||
                !string.Equals(match.StrategyVersion, record.StrategyVersion, StringComparison.Ordinal) ||
                !string.Equals(match.CanonicalSymbol, record.CanonicalSymbol, StringComparison.Ordinal) ||
                match.Direction != record.Direction || match.Volume != record.RequestedVolume)
            {
                items.Add(new(ReconciliationStatus.AmbiguousOwnership, record.SignalId, match.PositionId,
                    "Owned-position metadata does not match the durable execution intent."));
                continue;
            }

            var status = record.State is ExecutionLifecycle.Submitted or ExecutionLifecycle.Indeterminate
                ? ReconciliationStatus.IndeterminateReconciled : ReconciliationStatus.MatchedAccepted;
            items.Add(new(status, record.SignalId, match.PositionId, "Durable execution matches broker position."));
            if (record.State is ExecutionLifecycle.Submitted or ExecutionLifecycle.Indeterminate)
                await journal.UpdateAsync(record.SignalId, ExecutionLifecycle.Reconciled,
                    new(OrderStatus.Accepted, "Indeterminate execution reconciled.", record.BrokerOrderId,
                        BrokerPositionId: match.PositionId), now, cancellationToken);
        }

        var knownSignals = records.Where(r => RequiresBrokerPosition(r.State))
            .Select(r => r.SignalId).ToHashSet();
        foreach (var position in owned.Where(p => p.SignalId.HasValue && !knownSignals.Contains(p.SignalId.Value)))
            items.Add(new(ReconciliationStatus.UnexpectedOwnedPosition, position.SignalId, position.PositionId,
                "Owned broker position has no durable execution record."));
        return new(accountIdentifier, items, now);
    }

    private static bool RequiresBrokerPosition(ExecutionLifecycle state) => state is
        ExecutionLifecycle.Submitted or ExecutionLifecycle.Accepted or ExecutionLifecycle.Indeterminate or ExecutionLifecycle.Reconciled;
}

public sealed record ExecutionReadiness(bool Ready, string Reason);
public interface IOperationalExecutionReadiness
{
    Task<ExecutionReadiness> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class StartupReconciliationGate
{
    private ReconciliationReport? report;
    public void Apply(ReconciliationReport completedReport) => report = completedReport;
    public void Reset() => report = null;

    public Task<ExecutionReadiness> CheckAsync(string accountIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentifier);
        if (report is null)
            return Task.FromResult(new ExecutionReadiness(false, "Startup reconciliation has not completed."));
        if (!string.Equals(report.AccountIdentifier, accountIdentifier, StringComparison.Ordinal))
            return Task.FromResult(new ExecutionReadiness(false,
                "Startup reconciliation was completed for a different account."));
        return Task.FromResult(report.IsSafeForNewSubmissions
            ? new ExecutionReadiness(true, "Startup reconciliation completed.")
            : new ExecutionReadiness(false, "Startup reconciliation contains unresolved discrepancies."));
    }
}

public sealed class ExternalExecutionReadiness(StartupReconciliationGate reconciliation,
    IOperationalExecutionReadiness operational)
{
    public async Task<ExecutionReadiness> CheckAsync(string accountIdentifier,
        CancellationToken cancellationToken = default)
    {
        var reconciliationState = await reconciliation.CheckAsync(accountIdentifier, cancellationToken);
        return reconciliationState.Ready
            ? await operational.CheckAsync(cancellationToken)
            : reconciliationState;
    }
}

public sealed record OperationalSafetyState(string Scope, bool EmergencyShutdown, DateTimeOffset? ActivatedAt,
    string? Reason);

public interface IOperationalSafetyStateRepository
{
    Task<OperationalSafetyState> LoadAsync(string scope, CancellationToken cancellationToken = default);
    Task SaveEmergencyShutdownAsync(string scope, string reason, DateTimeOffset activatedAt,
        CancellationToken cancellationToken = default);
    Task ClearEmergencyShutdownAsync(string scope, CancellationToken cancellationToken = default);
}
