using GoldAiTrader.Core;

namespace GoldAiTrader.Risk;

public interface IRiskStateRepository
{
    Task<RiskState> LoadAsync(string scope, CancellationToken cancellationToken = default);
    Task SaveAsync(string scope, RiskState state, CancellationToken cancellationToken = default);
}

public sealed class PersistedRiskSession
{
    private readonly RiskEngine engine;
    private readonly RiskOptions options;
    private readonly IRiskStateRepository repository;
    private readonly string scope;
    private RiskState state = new();

    public PersistedRiskSession(RiskOptions options, IRiskStateRepository repository, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        options.Validate();
        this.options = options;
        this.repository = repository;
        this.scope = scope;
        engine = new(options);
    }

    public bool IsRestored { get; private set; }
    public RiskState State => state;

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        IsRestored = false;
        var restored = await repository.LoadAsync(scope, cancellationToken);
        state = restored;
        IsRestored = true;
    }

    public RiskAssessment AssessExecutable(TradeSetup setup, AccountSnapshot account,
        SymbolSpecification symbol, decimal spread, DateTimeOffset? evaluationTime = null) =>
        IsRestored
            ? engine.Assess(setup, account, symbol, spread, state, evaluationTime)
            : new(false, "Persisted risk state has not been restored.");

    public async Task<RiskState> RecordClosedTradeAsync(decimal profitLoss, DateTimeOffset closedAt,
        CancellationToken cancellationToken = default)
    {
        if (!IsRestored)
            throw new InvalidOperationException("Persisted risk state has not been restored.");

        var updated = RiskStateTracker.RecordClosedTrade(state, profitLoss, closedAt, options);
        await repository.SaveAsync(scope, updated, cancellationToken);
        state = updated;
        return state;
    }
}
