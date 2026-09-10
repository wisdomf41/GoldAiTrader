using Microsoft.EntityFrameworkCore;

namespace GoldAiTrader.Data;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<TradeEntity> Trades => Set<TradeEntity>();
    public DbSet<FeatureEntity> Features => Set<FeatureEntity>();
    public DbSet<DecisionEntity> Decisions => Set<DecisionEntity>();
    public DbSet<PredictionEntity> Predictions => Set<PredictionEntity>();
    public DbSet<ResearchRunEntity> ResearchRuns => Set<ResearchRunEntity>();
    public DbSet<ModelMetadataEntity> Models => Set<ModelMetadataEntity>();
    public DbSet<ExecutionJournalEntity> ExecutionJournal => Set<ExecutionJournalEntity>();
    public DbSet<RiskStateEntity> RiskStates => Set<RiskStateEntity>();
    public DbSet<OperationalSafetyStateEntity> OperationalSafetyStates => Set<OperationalSafetyStateEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<TradeEntity>().HasKey(x => x.Id);
        builder.Entity<FeatureEntity>().HasKey(x => x.Id);
        builder.Entity<DecisionEntity>().HasKey(x => x.Id);
        builder.Entity<PredictionEntity>().HasKey(x => x.Id);
        builder.Entity<ResearchRunEntity>().HasKey(x => x.Id);
        builder.Entity<ModelMetadataEntity>().HasKey(x => x.Id);
        builder.Entity<ExecutionJournalEntity>().HasKey(x => x.Id);
        builder.Entity<RiskStateEntity>().HasKey(x => x.Scope);
        builder.Entity<OperationalSafetyStateEntity>().HasKey(x => x.Scope);
        builder.Entity<TradeEntity>().HasIndex(x => x.SignalId).IsUnique();
        builder.Entity<ModelMetadataEntity>().HasIndex(x => x.Version).IsUnique();
        builder.Entity<ExecutionJournalEntity>().HasIndex(x => x.SignalId).IsUnique();
        builder.Entity<ExecutionJournalEntity>().HasIndex(x => x.ClientCorrelationId).IsUnique();
        builder.Entity<ExecutionJournalEntity>().HasIndex(x => x.BrokerPositionId).IsUnique();
    }
}

public sealed class TradeEntity
{
    public Guid Id { get; set; }
    public Guid SignalId { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public DateTimeOffset EntryTime { get; set; }
    public DateTimeOffset? ExitTime { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal Volume { get; set; }
    public decimal ProfitLoss { get; set; }
}
public sealed class FeatureEntity { public Guid Id { get; set; } public Guid SignalId { get; set; } public DateTimeOffset Time { get; set; } public string SchemaVersion { get; set; } = ""; public string PayloadJson { get; set; } = ""; }
public sealed class DecisionEntity { public Guid Id { get; set; } public Guid SignalId { get; set; } public DateTimeOffset Time { get; set; } public string Action { get; set; } = ""; public string Reason { get; set; } = ""; }
public sealed class PredictionEntity { public Guid Id { get; set; } public Guid SignalId { get; set; } public string ModelVersion { get; set; } = ""; public float Probability { get; set; } }
public sealed class ResearchRunEntity { public Guid Id { get; set; } public DateTimeOffset StartedAt { get; set; } public string ConfigurationJson { get; set; } = ""; public string ResultJson { get; set; } = ""; }
public sealed class ModelMetadataEntity { public Guid Id { get; set; } public string Version { get; set; } = ""; public string FeatureSchemaVersion { get; set; } = ""; public DateTimeOffset TrainedThrough { get; set; } public string DatasetVersion { get; set; } = ""; }
