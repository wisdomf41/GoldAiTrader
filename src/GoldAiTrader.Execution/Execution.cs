using GoldAiTrader.Core;

namespace GoldAiTrader.Execution;

public enum ExecutionMode { Backtest, Shadow, Demo, Live }
public enum OrderStatus { Accepted, Rejected, Duplicate, Indeterminate }
public enum RetryDisposition { NotRetryable, SafeToRetry }
public enum ExecutionSafetyStatus { None, SlippageLimitExceeded }

public sealed record ExecutionOptions(ExecutionMode Mode = ExecutionMode.Shadow, bool DemoOnly = true,
    int MaximumRetries = 2, decimal MaximumSlippage = 0.50m, bool TradingEnabled = false)
{
    public void Validate()
    {
        if (Mode == ExecutionMode.Live || !DemoOnly) throw new InvalidOperationException("Live trading is disabled.");
        if (TradingEnabled && Mode != ExecutionMode.Demo) throw new InvalidOperationException("External trading requires Demo mode.");
        if (MaximumRetries is < 0 or > 5 || MaximumSlippage < 0) throw new InvalidOperationException("Invalid execution options.");
    }
}

public sealed record ApprovedOrder(TradeSetup Setup, PositionSize PositionSize);
public sealed record ExecutionResult(OrderStatus Status, string Reason, string? BrokerOrderId = null,
    decimal Slippage = 0, string? BrokerPositionId = null,
    RetryDisposition RetryDisposition = RetryDisposition.NotRetryable,
    ExecutionSafetyStatus SafetyStatus = ExecutionSafetyStatus.None);

public sealed record ExecutionAccountState(string AccountIdentifier, AccountEnvironment Environment, bool BrokerConnected)
{
    public bool IsVerifiedDemo => !string.IsNullOrWhiteSpace(AccountIdentifier) &&
        Environment == AccountEnvironment.Demo && BrokerConnected;
}

public interface ITradeExecutor
{
    Task<ExecutionResult> SubmitAsync(ApprovedOrder order, CancellationToken cancellationToken = default);
    Task<ExecutionResult> CloseAsync(string positionId, CancellationToken cancellationToken = default);
    Task<ExecutionResult> ModifyAsync(string positionId, decimal stopLoss, decimal takeProfit, CancellationToken cancellationToken = default);
}

public sealed class SimulatedTradeExecutor(ExecutionOptions options) : ITradeExecutor
{
    private readonly HashSet<Guid> submittedSignals = [];
    public Task<ExecutionResult> SubmitAsync(ApprovedOrder order, CancellationToken cancellationToken = default)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!submittedSignals.Add(order.Setup.SignalId))
            return Task.FromResult(new ExecutionResult(OrderStatus.Duplicate, "Signal already submitted."));
        return Task.FromResult(new ExecutionResult(OrderStatus.Accepted, "Simulated order accepted.", $"SIM-{order.Setup.SignalId:N}"));
    }

    public Task<ExecutionResult> CloseAsync(string positionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExecutionResult(string.IsNullOrWhiteSpace(positionId) ? OrderStatus.Rejected : OrderStatus.Accepted,
            string.IsNullOrWhiteSpace(positionId) ? "Position id required." : "Simulated close accepted."));

    public Task<ExecutionResult> ModifyAsync(string positionId, decimal stopLoss, decimal takeProfit, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExecutionResult(string.IsNullOrWhiteSpace(positionId) || stopLoss <= 0 || takeProfit <= 0 ? OrderStatus.Rejected : OrderStatus.Accepted,
            "Simulated modification evaluated."));
}

public interface IExecutionGateway
{
    string ProviderName { get; }
    Task<ExecutionAccountState> GetExecutionAccountAsync(CancellationToken cancellationToken);
    Task<ExecutionResult> SubmitAsync(ApprovedOrder order, CancellationToken cancellationToken);
    Task<ExecutionResult> CloseAsync(string positionId, CancellationToken cancellationToken);
    Task<ExecutionResult> ModifyAsync(string positionId, decimal stopLoss, decimal takeProfit, CancellationToken cancellationToken);
}

public sealed class GatewayTradeExecutor(ExecutionOptions options, IExecutionGateway gateway,
    IExecutionJournal? journal = null, ExternalExecutionReadiness? readiness = null,
    IPositionProvider? positionProvider = null) : ITradeExecutor
{
    public async Task<ExecutionResult> SubmitAsync(ApprovedOrder order, CancellationToken cancellationToken = default)
    {
        var verification = await ValidateExternalExecutionAsync(cancellationToken);
        if (verification.Failure is not null) return verification.Failure;

        var now = DateTimeOffset.UtcNow;
        var correlationId = $"GoldAiTrader-{order.Setup.SignalId:N}";
        var record = new ExecutionJournalRecord(order.Setup.SignalId, correlationId, order.Setup.Symbol,
            verification.Account!.AccountIdentifier, order.Setup.StrategyVersion, order.Setup.Direction,
            order.PositionSize.Volume, order.Setup.EntryPrice, order.Setup.StopLoss, order.Setup.TakeProfit,
            ExecutionLifecycle.Reserved, null, null, now, now);
        if (!await journal!.TryReserveAsync(record, cancellationToken))
            return new(OrderStatus.Duplicate, "Signal already exists in the durable execution journal.");

        ExecutionResult result = new(OrderStatus.Rejected, "No attempt made.");
        for (var attempt = 0; attempt <= options.MaximumRetries; attempt++)
        {
            await journal.UpdateAsync(order.Setup.SignalId, ExecutionLifecycle.Submitted, null,
                DateTimeOffset.UtcNow, cancellationToken);
            try
            {
                result = await gateway.SubmitAsync(order, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = new(OrderStatus.Indeterminate,
                    "Broker outcome is unknown because submission was cancelled.");
                await journal.UpdateAsync(order.Setup.SignalId, ExecutionLifecycle.Indeterminate,
                    result, DateTimeOffset.UtcNow, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                result = new(OrderStatus.Indeterminate, $"Broker outcome is unknown: {ex.Message}");
                await journal.UpdateAsync(order.Setup.SignalId, ExecutionLifecycle.Indeterminate, result,
                    DateTimeOffset.UtcNow, cancellationToken);
                return result;
            }

            var lifecycle = result.Status switch
            {
                OrderStatus.Accepted => ExecutionLifecycle.Accepted,
                OrderStatus.Indeterminate => ExecutionLifecycle.Indeterminate,
                _ => ExecutionLifecycle.Rejected
            };
            await journal.UpdateAsync(order.Setup.SignalId, lifecycle, result, DateTimeOffset.UtcNow, cancellationToken);
            if (result.Status != OrderStatus.Rejected ||
                result.RetryDisposition != RetryDisposition.SafeToRetry) break;
        }

        return result.Status == OrderStatus.Accepted && result.Slippage > options.MaximumSlippage
            ? result with { Reason = "Execution completed with slippage above the configured limit.",
                SafetyStatus = ExecutionSafetyStatus.SlippageLimitExceeded }
            : result;
    }

    public async Task<ExecutionResult> CloseAsync(string positionId, CancellationToken cancellationToken = default)
    {
        var verification = await ValidateExternalExecutionAsync(cancellationToken);
        if (verification.Failure is not null) return verification.Failure;
        var ownershipFailure = await ValidateOwnedPositionAsync(positionId, cancellationToken);
        return ownershipFailure ?? await gateway.CloseAsync(positionId, cancellationToken);
    }

    public async Task<ExecutionResult> ModifyAsync(string positionId, decimal stopLoss, decimal takeProfit, CancellationToken cancellationToken = default)
    {
        var verification = await ValidateExternalExecutionAsync(cancellationToken);
        if (verification.Failure is not null) return verification.Failure;
        var ownershipFailure = await ValidateOwnedPositionAsync(positionId, cancellationToken);
        return ownershipFailure ?? await gateway.ModifyAsync(positionId, stopLoss, takeProfit, cancellationToken);
    }

    private async Task<ExecutionResult?> ValidateOwnedPositionAsync(string positionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(positionId))
            return new(OrderStatus.Rejected, "Position id is required.");
        if (positionProvider is null)
            return new(OrderStatus.Rejected, "Position inventory is required before managing broker positions.");

        IReadOnlyList<PositionSnapshot> positions;
        try { positions = await positionProvider.GetPositionsAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(OrderStatus.Rejected, $"Position ownership verification failed: {ex.Message}"); }

        var position = positions.SingleOrDefault(p =>
            string.Equals(p.PositionId, positionId, StringComparison.Ordinal));
        if (position is null)
            return new(OrderStatus.Rejected, "Position was not found in the verified broker inventory.");
        return PositionOwnership.Classify(position) == PositionOwnershipStatus.Owned
            ? null
            : new(OrderStatus.Rejected, "Only unambiguously owned GoldAiTrader positions may be managed.");
    }

    private async Task<ExecutionVerification> ValidateExternalExecutionAsync(CancellationToken cancellationToken)
    {
        try { options.Validate(); }
        catch (Exception ex) { return new(new(OrderStatus.Rejected, ex.Message), null); }
        if (options.Mode != ExecutionMode.Demo || !options.TradingEnabled)
            return new(new(OrderStatus.Rejected, "External execution requires explicitly enabled Demo mode."), null);
        if (journal is null || readiness is null)
            return new(new(OrderStatus.Rejected,
                "Durable journal and mandatory external execution readiness are required."), null);

        ExecutionAccountState account;
        try { account = await gateway.GetExecutionAccountAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(new(OrderStatus.Rejected, $"Account verification failed: {ex.Message}"), null); }

        if (string.IsNullOrWhiteSpace(account.AccountIdentifier))
            return new(new(OrderStatus.Rejected, "Execution account identity is unavailable."), null);
        if (account.Environment == AccountEnvironment.Unknown)
            return new(new(OrderStatus.Rejected, "Execution account environment is unknown."), null);
        if (account.Environment != AccountEnvironment.Demo)
            return new(new(OrderStatus.Rejected, "Execution account is not Demo."), null);
        if (!account.BrokerConnected)
            return new(new(OrderStatus.Rejected, "Broker connection is not healthy."), null);

        var readinessState = await readiness.CheckAsync(account.AccountIdentifier, cancellationToken);
        return readinessState.Ready
            ? new(null, account)
            : new(new(OrderStatus.Rejected, readinessState.Reason), null);
    }

    private sealed record ExecutionVerification(ExecutionResult? Failure, ExecutionAccountState? Account);
}
