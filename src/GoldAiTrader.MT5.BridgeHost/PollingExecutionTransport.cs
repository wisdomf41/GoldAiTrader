using System.Collections.Concurrent;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Execution;

namespace GoldAiTrader.MT5.BridgeHost;

public sealed class MT5BridgeHostRuntime : IHostedService
{
    private volatile bool healthy;

    public bool Healthy => healthy;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        healthy = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        healthy = false;
        return Task.CompletedTask;
    }
}

public enum MT5AcknowledgementDecision
{
    Accepted,
    Duplicate,
    UnknownCommand,
    CommandNotDelivered,
    IndeterminateCommand,
    Invalid
}

public sealed class MT5PollingExecutionTransport : IMT5LoopbackExecutionTransport
{
    private readonly MT5BridgeHostOptions options;
    private readonly MT5BridgeIdentity identity;
    private readonly MT5BridgeState state;
    private readonly MT5BridgeHostRuntime runtime;
    private readonly MT5AuthenticatedSession session;
    private readonly TimeProvider timeProvider;
    private readonly object commandQueueSync = new();
    private readonly Queue<Guid> commandQueue = [];
    private readonly ConcurrentDictionary<Guid, PendingCommand> pending = new();
    private readonly SemaphoreSlim pendingSlots;
    private readonly object terminalSync = new();
    private readonly Dictionary<Guid, TerminalCommandState> terminalCommands = [];
    private readonly Queue<Guid> terminalOrder = [];

    public MT5PollingExecutionTransport(MT5BridgeHostOptions options,
        MT5BridgeIdentity identity, MT5BridgeState state,
        MT5BridgeHostRuntime runtime, MT5AuthenticatedSession session,
        TimeProvider timeProvider)
    {
        this.options = options;
        this.identity = identity;
        this.state = state;
        this.runtime = runtime;
        this.session = session;
        this.timeProvider = timeProvider;
        pendingSlots = new(options.PendingCommandCapacity, options.PendingCommandCapacity);
    }

    public MT5LoopbackEndpoint Endpoint => options.Endpoint;
    public bool SupportsClientCorrelationIds => true;
    public bool SupportsOwnershipMetadata => true;
    public bool IsAvailable => options.ExternalExecutionEnabled && runtime.Healthy &&
        state.GetHealth().Healthy;
    public bool IsAuthenticated => session.IsCurrent;
    public int PendingCount => pending.Count;

    public async Task<MT5ExecutionAcknowledgement> SendAsync(MT5ExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        MT5BridgeProtocol.ValidateEnvelope(command.Envelope, identity);
        if (command.CommandId == Guid.Empty ||
            !string.Equals(command.AccountIdentifier, identity.AccountIdentifier,
                StringComparison.Ordinal))
            throw new InvalidOperationException("MT5 execution command identity is invalid.");

        var executionState = state.GetExecutionSnapshot();
        if (!IsAvailable || !IsAuthenticated || !executionState.Ready)
            return CreateLocalAcknowledgement(command.CommandId, OrderStatus.Rejected,
                "MT5 command transport is not ready; no command was queued.");
        if (IsKnownTerminalCommand(command.CommandId))
            return CreateLocalAcknowledgement(command.CommandId, OrderStatus.Rejected,
                "MT5 command ID was already used and cannot be queued again.");
        if (!pendingSlots.Wait(0))
            return CreateLocalAcknowledgement(command.CommandId, OrderStatus.Rejected,
                "MT5 pending-command capacity is exhausted; no command was queued.");

        var item = new PendingCommand(command);
        var added = pending.TryAdd(command.CommandId, item);
        if (!added || !TryEnqueue(command.CommandId))
        {
            if (added)
                pending.TryRemove(command.CommandId, out _);
            pendingSlots.Release();
            return CreateLocalAcknowledgement(command.CommandId, OrderStatus.Rejected,
                "MT5 command could not be queued safely.");
        }

        try
        {
            return await item.Completion.Task.WaitAsync(
                options.CommandTimeout, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var finalization = item.FinalizeAfterWaitFailure();
            if (finalization.Acknowledgement is not null)
                return finalization.Acknowledgement;
            if (finalization.WasDelivered)
            {
                RememberTerminal(command.CommandId,
                    TerminalCommandState.IndeterminateDelivered);
                return CreateLocalAcknowledgement(command.CommandId,
                    OrderStatus.Indeterminate,
                    "MT5 command was cancelled after delivery; broker outcome may be unknown and reconciliation is required.");
            }

            RememberTerminal(command.CommandId, TerminalCommandState.NotDelivered);
            throw;
        }
        catch (TimeoutException)
        {
            var finalization = item.FinalizeAfterWaitFailure();
            if (finalization.Acknowledgement is not null)
                return finalization.Acknowledgement;
            if (finalization.WasDelivered)
            {
                RememberTerminal(command.CommandId,
                    TerminalCommandState.IndeterminateDelivered);
                return CreateLocalAcknowledgement(command.CommandId,
                    OrderStatus.Indeterminate,
                    "MT5 command acknowledgement timed out after delivery; broker outcome is unknown and reconciliation is required.");
            }

            RememberTerminal(command.CommandId, TerminalCommandState.NotDelivered);
            return CreateLocalAcknowledgement(command.CommandId, OrderStatus.Rejected,
                "MT5 command acknowledgement timed out before delivery; the command was not delivered to MT5.");
        }
        finally
        {
            pending.TryRemove(command.CommandId, out _);
            RemoveQueuedCommand(command.CommandId);
            pendingSlots.Release();
        }
    }

    public bool TryPollNext(string bridgeInstanceId, string protocolVersion,
        out MT5ExecutionCommand? command)
    {
        command = null;
        if (!IsAvailable || !IsAuthenticated || !state.GetExecutionSnapshot().Ready ||
            !string.Equals(bridgeInstanceId, identity.BridgeInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(protocolVersion, identity.ProtocolVersion,
                StringComparison.Ordinal))
            return false;

        lock (commandQueueSync)
        {
            while (commandQueue.Count > 0)
            {
                var commandId = commandQueue.Dequeue();
                if (!pending.TryGetValue(commandId, out var item) || !item.TryClaim())
                    continue;
                command = item.Command;
                return true;
            }
        }

        return false;
    }

    public MT5AcknowledgementDecision AcceptAcknowledgement(
        MT5ExecutionAcknowledgement acknowledgement)
    {
        if (acknowledgement is null || acknowledgement.CommandId == Guid.Empty ||
            acknowledgement.Result is null ||
            !Enum.IsDefined(acknowledgement.Result.Status) ||
            string.IsNullOrWhiteSpace(acknowledgement.Result.Reason))
            return MT5AcknowledgementDecision.Invalid;

        try
        {
            MT5BridgeProtocol.ValidateEnvelope(acknowledgement.Envelope, identity);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or NotSupportedException)
        {
            return MT5AcknowledgementDecision.Invalid;
        }

        var age = timeProvider.GetUtcNow().ToUniversalTime() -
            acknowledgement.Envelope.SentAtUtc;
        if (age < -options.AuthenticationTolerance || age > options.AuthenticationTolerance)
            return MT5AcknowledgementDecision.Invalid;

        lock (terminalSync)
        {
            if (terminalCommands.TryGetValue(acknowledgement.CommandId,
                    out var terminalState))
                return DecisionFor(terminalState);
        }

        if (!pending.TryGetValue(acknowledgement.CommandId, out var item))
            return MT5AcknowledgementDecision.UnknownCommand;

        return item.TryAcknowledge(acknowledgement, () =>
            RememberTerminal(acknowledgement.CommandId,
                TerminalCommandState.Acknowledged));
    }

    private bool TryEnqueue(Guid commandId)
    {
        lock (commandQueueSync)
        {
            RemoveStaleQueuedCommands();
            if (commandQueue.Count >= options.PendingCommandCapacity)
                return false;
            commandQueue.Enqueue(commandId);
            return true;
        }
    }

    private void RemoveQueuedCommand(Guid commandId)
    {
        lock (commandQueueSync)
        {
            var count = commandQueue.Count;
            for (var index = 0; index < count; index++)
            {
                var queuedId = commandQueue.Dequeue();
                if (queuedId != commandId && pending.ContainsKey(queuedId))
                    commandQueue.Enqueue(queuedId);
            }
        }
    }

    private void RemoveStaleQueuedCommands()
    {
        var count = commandQueue.Count;
        for (var index = 0; index < count; index++)
        {
            var commandId = commandQueue.Dequeue();
            if (pending.ContainsKey(commandId))
                commandQueue.Enqueue(commandId);
        }
    }

    private bool IsKnownTerminalCommand(Guid commandId)
    {
        lock (terminalSync)
            return terminalCommands.ContainsKey(commandId);
    }

    private void RememberTerminal(Guid commandId, TerminalCommandState state)
    {
        lock (terminalSync)
        {
            if (terminalCommands.ContainsKey(commandId))
                return;
            terminalCommands.Add(commandId, state);
            terminalOrder.Enqueue(commandId);
            while (terminalOrder.Count > options.PendingCommandCapacity * 2)
                terminalCommands.Remove(terminalOrder.Dequeue());
        }
    }

    private static MT5AcknowledgementDecision DecisionFor(TerminalCommandState state) =>
        state switch
        {
            TerminalCommandState.Acknowledged => MT5AcknowledgementDecision.Duplicate,
            TerminalCommandState.IndeterminateDelivered =>
                MT5AcknowledgementDecision.IndeterminateCommand,
            _ => MT5AcknowledgementDecision.CommandNotDelivered
        };

    private MT5ExecutionAcknowledgement CreateLocalAcknowledgement(Guid commandId,
        OrderStatus status, string reason) =>
        new(new(MT5BridgeProtocol.CurrentVersion, identity.BridgeInstanceId,
                timeProvider.GetUtcNow().ToUniversalTime()),
            commandId, new(status, reason));

    private enum PendingCommandState
    {
        Queued,
        Delivered,
        Acknowledged,
        NotDelivered,
        IndeterminateDelivered
    }

    private enum TerminalCommandState
    {
        Acknowledged,
        NotDelivered,
        IndeterminateDelivered
    }

    private readonly record struct WaitFailureFinalization(bool WasDelivered,
        MT5ExecutionAcknowledgement? Acknowledgement);

    private sealed class PendingCommand(MT5ExecutionCommand command)
    {
        private readonly object sync = new();
        private PendingCommandState state = PendingCommandState.Queued;
        private MT5ExecutionAcknowledgement? acknowledgement;

        public MT5ExecutionCommand Command { get; } = command;
        public TaskCompletionSource<MT5ExecutionAcknowledgement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryClaim()
        {
            lock (sync)
            {
                if (state != PendingCommandState.Queued)
                    return false;
                state = PendingCommandState.Delivered;
                return true;
            }
        }

        public MT5AcknowledgementDecision TryAcknowledge(
            MT5ExecutionAcknowledgement value, Action beforeCompletion)
        {
            lock (sync)
            {
                switch (state)
                {
                    case PendingCommandState.Queued:
                    case PendingCommandState.NotDelivered:
                        return MT5AcknowledgementDecision.CommandNotDelivered;
                    case PendingCommandState.IndeterminateDelivered:
                        return MT5AcknowledgementDecision.IndeterminateCommand;
                    case PendingCommandState.Acknowledged:
                        return MT5AcknowledgementDecision.Duplicate;
                    case PendingCommandState.Delivered:
                        acknowledgement = value;
                        state = PendingCommandState.Acknowledged;
                        beforeCompletion();
                        Completion.TrySetResult(value);
                        return MT5AcknowledgementDecision.Accepted;
                    default:
                        throw new InvalidOperationException(
                            "Unsupported MT5 pending-command state.");
                }
            }
        }

        public WaitFailureFinalization FinalizeAfterWaitFailure()
        {
            lock (sync)
            {
                if (state == PendingCommandState.Acknowledged)
                    return new(false, acknowledgement);
                if (state == PendingCommandState.Delivered)
                {
                    state = PendingCommandState.IndeterminateDelivered;
                    return new(true, null);
                }

                state = PendingCommandState.NotDelivered;
                return new(false, null);
            }
        }
    }
}
