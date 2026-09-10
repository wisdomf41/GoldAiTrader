using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.Risk;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace GoldAiTrader.Data;

public sealed class ExecutionJournalEntity
{
    public Guid Id { get; set; }
    public Guid SignalId { get; set; }
    public string ClientCorrelationId { get; set; } = "";
    public string CanonicalSymbol { get; set; } = "";
    public string AccountIdentifier { get; set; } = "";
    public string StrategyVersion { get; set; } = "";
    public string Direction { get; set; } = "";
    public decimal RequestedVolume { get; set; }
    public decimal RequestedEntry { get; set; }
    public decimal RequestedStopLoss { get; set; }
    public decimal RequestedTakeProfit { get; set; }
    public string State { get; set; } = "";
    public string? BrokerOrderId { get; set; }
    public string? BrokerPositionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int SubmissionAttempts { get; set; }
    public string? LastMessage { get; set; }
}

public sealed class RiskStateEntity
{
    public string Scope { get; set; } = "";
    public int ConsecutiveLosses { get; set; }
    public DateTimeOffset? SuspendedUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OperationalSafetyStateEntity
{
    public string Scope { get; set; } = "";
    public bool EmergencyShutdown { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EfExecutionJournal(TradingDbContext db) : IExecutionJournal
{
    public async Task<bool> TryReserveAsync(ExecutionJournalRecord record, CancellationToken cancellationToken = default)
    {
        db.ExecutionJournal.Add(ToEntity(record));
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<ExecutionJournalRecord?> GetAsync(Guid signalId, CancellationToken cancellationToken = default)
    {
        var entity = await db.ExecutionJournal.AsNoTracking().SingleOrDefaultAsync(x => x.SignalId == signalId, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<ExecutionJournalRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await db.ExecutionJournal.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken)).Select(ToDomain).ToArray();

    public async Task<IReadOnlyList<ExecutionJournalRecord>> GetByAccountAsync(string accountIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentifier);
        return (await db.ExecutionJournal.AsNoTracking()
            .Where(x => x.AccountIdentifier == accountIdentifier)
            .OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken)).Select(ToDomain).ToArray();
    }

    public async Task UpdateAsync(Guid signalId, ExecutionLifecycle state, ExecutionResult? result,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        var entity = await db.ExecutionJournal.SingleOrDefaultAsync(x => x.SignalId == signalId, cancellationToken)
            ?? throw new InvalidOperationException("Execution reservation does not exist.");
        entity.State = state.ToString();
        entity.UpdatedAt = updatedAt;
        entity.BrokerOrderId = result?.BrokerOrderId ?? entity.BrokerOrderId;
        entity.BrokerPositionId = result?.BrokerPositionId ?? entity.BrokerPositionId;
        entity.LastMessage = result?.Reason ?? entity.LastMessage;
        if (state == ExecutionLifecycle.Submitted) entity.SubmissionAttempts++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ExecutionJournalEntity ToEntity(ExecutionJournalRecord record) => new()
    {
        Id = Guid.NewGuid(), SignalId = record.SignalId, ClientCorrelationId = record.ClientCorrelationId,
        CanonicalSymbol = record.CanonicalSymbol, AccountIdentifier = record.AccountIdentifier,
        StrategyVersion = record.StrategyVersion, Direction = record.Direction.ToString(),
        RequestedVolume = record.RequestedVolume, RequestedEntry = record.RequestedEntry,
        RequestedStopLoss = record.RequestedStopLoss, RequestedTakeProfit = record.RequestedTakeProfit,
        State = record.State.ToString(), BrokerOrderId = record.BrokerOrderId,
        BrokerPositionId = record.BrokerPositionId, CreatedAt = record.CreatedAt, UpdatedAt = record.UpdatedAt,
        SubmissionAttempts = record.SubmissionAttempts, LastMessage = record.LastMessage
    };

    private static ExecutionJournalRecord ToDomain(ExecutionJournalEntity entity) => new(entity.SignalId,
        entity.ClientCorrelationId, entity.CanonicalSymbol, entity.AccountIdentifier, entity.StrategyVersion,
        Enum.Parse<TradeDirection>(entity.Direction), entity.RequestedVolume, entity.RequestedEntry,
        entity.RequestedStopLoss, entity.RequestedTakeProfit, Enum.Parse<ExecutionLifecycle>(entity.State),
        entity.BrokerOrderId, entity.BrokerPositionId, entity.CreatedAt, entity.UpdatedAt,
        entity.SubmissionAttempts, entity.LastMessage);
}

public sealed class EfRiskStateRepository(TradingDbContext db) : IRiskStateRepository
{
    public async Task<RiskState> LoadAsync(string scope, CancellationToken cancellationToken = default)
    {
        var entity = await db.RiskStates.AsNoTracking().SingleOrDefaultAsync(x => x.Scope == scope, cancellationToken);
        return entity is null ? new() : new(entity.ConsecutiveLosses, entity.SuspendedUntil);
    }

    public async Task SaveAsync(string scope, RiskState state, CancellationToken cancellationToken = default)
    {
        var entity = await db.RiskStates.SingleOrDefaultAsync(x => x.Scope == scope, cancellationToken);
        if (entity is null) db.RiskStates.Add(new() { Scope = scope, ConsecutiveLosses = state.ConsecutiveLosses,
            SuspendedUntil = state.SuspendedUntil, UpdatedAt = DateTimeOffset.UtcNow });
        else { entity.ConsecutiveLosses = state.ConsecutiveLosses; entity.SuspendedUntil = state.SuspendedUntil; entity.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfOperationalSafetyStateRepository(TradingDbContext db) : IOperationalSafetyStateRepository
{
    public async Task<OperationalSafetyState> LoadAsync(string scope, CancellationToken cancellationToken = default)
    {
        var entity = await db.OperationalSafetyStates.AsNoTracking().SingleOrDefaultAsync(x => x.Scope == scope, cancellationToken);
        return entity is null ? new(scope, false, null, null) : new(scope, entity.EmergencyShutdown, entity.ActivatedAt, entity.Reason);
    }

    public Task SaveEmergencyShutdownAsync(string scope, string reason, DateTimeOffset activatedAt,
        CancellationToken cancellationToken = default) => SaveAsync(scope, true, reason, activatedAt, cancellationToken);

    public Task ClearEmergencyShutdownAsync(string scope, CancellationToken cancellationToken = default) =>
        SaveAsync(scope, false, "Explicit administrative reset.", null, cancellationToken);

    private async Task SaveAsync(string scope, bool active, string reason, DateTimeOffset? activatedAt,
        CancellationToken cancellationToken)
    {
        var entity = await db.OperationalSafetyStates.SingleOrDefaultAsync(x => x.Scope == scope, cancellationToken);
        if (entity is null) db.OperationalSafetyStates.Add(new() { Scope = scope, EmergencyShutdown = active,
            Reason = reason, ActivatedAt = activatedAt, UpdatedAt = DateTimeOffset.UtcNow });
        else { entity.EmergencyShutdown = active; entity.Reason = reason; entity.ActivatedAt = activatedAt; entity.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class TradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("GOLDAITRADER_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=goldaitrader;Username=postgres";
        return new(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(connection).Options);
    }
}
