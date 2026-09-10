using GoldAiTrader.Execution;

namespace GoldAiTrader.Bot;

public sealed record TradingEnvironmentOptions(ExecutionMode Mode = ExecutionMode.Shadow, string CanonicalSymbol = "XAUUSD",
    string ExecutionTimeframe = "M5", string ContextTimeframe = "M15", bool EnableTrading = false,
    bool EnableLiveTrading = false, TimeSpan? StaleAfter = null)
{
    public TimeSpan EffectiveStaleAfter => StaleAfter ?? TimeSpan.FromMinutes(10);
}

public sealed record OperationalStatus(bool Healthy, bool AcceptingNewTrades, string Reason,
    DateTimeOffset? LastMarketData, bool BrokerConnected, bool EmergencyShutdown);

public sealed class StartupValidator
{
    public IReadOnlyList<string> Validate(TradingEnvironmentOptions environment, ExecutionOptions execution)
    {
        var errors = new List<string>();
        if (environment.CanonicalSymbol != "XAUUSD") errors.Add("TrendPullbackV1 supports canonical XAUUSD only.");
        if (environment.ExecutionTimeframe != "M5" || environment.ContextTimeframe != "M15") errors.Add("Required M5/M15 timeframes are not configured.");
        if (environment.Mode == ExecutionMode.Live || environment.EnableLiveTrading) errors.Add("Live trading is not approved.");
        if (environment.EnableTrading && environment.Mode is not ExecutionMode.Demo) errors.Add("Order execution is allowed only in explicit Demo mode.");
        try { execution.Validate(); } catch (Exception ex) { errors.Add(ex.Message); }
        return errors;
    }
}

public sealed class OperationalGuard(TradingEnvironmentOptions options,
    IOperationalSafetyStateRepository? safetyStateRepository = null, string safetyScope = "default",
    TimeProvider? timeProvider = null) : IOperationalExecutionReadiness
{
    private bool shuttingDown;
    private bool emergencyShutdown;
    private bool safetyStateRestored = safetyStateRepository is null;
    private bool brokerConnected;
    private DateTimeOffset? lastMarketData;

    public void RecordMarketData(DateTimeOffset timestamp)
    { if (lastMarketData is null || timestamp > lastMarketData.Value) lastMarketData = timestamp; }
    public void SetBrokerConnected(bool connected) => brokerConnected = connected;
    public void TriggerEmergencyShutdown()
    {
        if (safetyStateRepository is not null)
            throw new InvalidOperationException("Use the durable asynchronous emergency-shutdown path.");
        emergencyShutdown = true;
    }
    public void BeginShutdown() => shuttingDown = true;

    public async Task RestoreSafetyStateAsync(CancellationToken cancellationToken = default)
    {
        if (safetyStateRepository is null)
        {
            safetyStateRestored = true;
            return;
        }
        safetyStateRestored = false;
        var state = await safetyStateRepository.LoadAsync(safetyScope, cancellationToken);
        emergencyShutdown = state.EmergencyShutdown;
        safetyStateRestored = true;
    }

    public async Task TriggerEmergencyShutdownAsync(string reason, DateTimeOffset activatedAt,
        CancellationToken cancellationToken = default)
    {
        emergencyShutdown = true;
        if (safetyStateRepository is not null)
            await safetyStateRepository.SaveEmergencyShutdownAsync(safetyScope, reason, activatedAt, cancellationToken);
    }

    public async Task ClearEmergencyShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (safetyStateRepository is not null)
            await safetyStateRepository.ClearEmergencyShutdownAsync(safetyScope, cancellationToken);
        emergencyShutdown = false;
        safetyStateRestored = true;
    }

    public Task<ExecutionReadiness> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = GetStatus((timeProvider ?? TimeProvider.System).GetUtcNow());
        return Task.FromResult(new ExecutionReadiness(status.AcceptingNewTrades, status.Reason));
    }

    public OperationalStatus GetStatus(DateTimeOffset now)
    {
        var stale = lastMarketData is null || now - lastMarketData > options.EffectiveStaleAfter;
        var accepting = safetyStateRestored && !shuttingDown && !emergencyShutdown && brokerConnected && !stale &&
            options.EnableTrading && options.Mode == ExecutionMode.Demo;
        var reason = !safetyStateRestored ? "Durable safety state has not been restored." :
            shuttingDown ? "Application shutting down." : emergencyShutdown ? "Emergency shutdown active." :
            !brokerConnected ? "Broker disconnected." : stale ? "Market data stale." :
            !options.EnableTrading ? "Trading disabled." : options.Mode != ExecutionMode.Demo ? "Non-executing mode." : "Healthy.";
        return new(safetyStateRestored && !shuttingDown && !emergencyShutdown && brokerConnected && !stale, accepting, reason, lastMarketData, brokerConnected, emergencyShutdown);
    }
}
