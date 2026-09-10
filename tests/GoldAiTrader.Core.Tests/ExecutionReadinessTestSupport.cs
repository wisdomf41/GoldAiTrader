using GoldAiTrader.Execution;

namespace GoldAiTrader.Core.Tests;

internal static class TestExecutionReadiness
{
    public static ExternalExecutionReadiness Ready(string accountIdentifier = "demo-account")
    {
        var reconciliation = new StartupReconciliationGate();
        reconciliation.Apply(new(accountIdentifier, [], DateTimeOffset.UtcNow));
        return new(reconciliation, new HealthyOperationalReadiness());
    }

    private sealed class HealthyOperationalReadiness : IOperationalExecutionReadiness
    {
        public Task<ExecutionReadiness> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecutionReadiness(true, "Ready."));
    }
}
