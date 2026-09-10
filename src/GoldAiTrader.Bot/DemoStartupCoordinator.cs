using GoldAiTrader.Execution;
using GoldAiTrader.Platform;
using GoldAiTrader.Risk;

namespace GoldAiTrader.Bot;

public sealed record DemoStartupResult(bool Ready, string Reason, ExecutionAccountState? Account = null,
    ReconciliationReport? Reconciliation = null);

public sealed class DemoStartupCoordinator(OperationalGuard operationalGuard,
    PersistedRiskSession riskSession, ITradingPlatformGateway platform,
    IExecutionJournal journal, StartupReconciliationGate reconciliationGate)
{
    public async Task<DemoStartupResult> StartAsync(DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        reconciliationGate.Reset();
        try
        {
            await operationalGuard.RestoreSafetyStateAsync(cancellationToken);
            await riskSession.RestoreAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, $"Durable state restoration failed: {ex.Message}");
        }

        var capabilityValidation = PlatformCapabilityValidator.Validate(platform, ExecutionMode.Demo);
        if (!capabilityValidation.IsSuitable)
            return new(false, $"Platform capability validation failed: {capabilityValidation.Summary}");

        var gateway = platform.ExecutionGateway!;

        ExecutionAccountState account;
        try
        {
            account = await gateway.GetExecutionAccountAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, $"Demo account verification failed: {ex.Message}");
        }

        operationalGuard.SetBrokerConnected(account.BrokerConnected);
        if (!account.IsVerifiedDemo)
            return new(false, "A connected, explicitly identified Demo account is required.", account);

        ReconciliationReport report;
        try
        {
            report = await new ExecutionReconciliationService(journal, platform.PositionProvider!)
                .ReconcileAsync(account.AccountIdentifier, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, $"Account reconciliation failed: {ex.Message}", account);
        }

        reconciliationGate.Apply(report);
        var reconciliationState = await reconciliationGate.CheckAsync(account.AccountIdentifier, cancellationToken);
        if (!reconciliationState.Ready)
            return new(false, reconciliationState.Reason, account, report);

        var operationalState = await operationalGuard.CheckAsync(cancellationToken);
        return operationalState.Ready
            ? new(true, "Operational startup sequence completed.", account, report)
            : new(false, operationalState.Reason, account, report);
    }
}
