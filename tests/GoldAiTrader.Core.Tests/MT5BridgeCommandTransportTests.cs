using System.Net;
using System.Text.Json;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.MT5.BridgeHost;

namespace GoldAiTrader.Core.Tests;

public sealed class MT5BridgeCommandTransportTests
{
    [Fact]
    public async Task ExternalExecutionIsDisabledByDefault()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.PrepareReadyStateAsync();

        var result = await host.Transport.SendAsync(Command(host));

        Assert.Equal(OrderStatus.Rejected, result.Result.Status);
        Assert.Equal(0, host.Transport.PendingCount);
    }

    [Fact]
    public async Task CommandCanBePolledAndAcknowledgementCompletesWaitingSend()
    {
        await using var host = await ExecutableHostAsync();
        var command = Command(host);
        var sendTask = host.Transport.SendAsync(command);

        using var pollRequest = host.SignedRequest(HttpMethod.Get,
            MT5BridgeRoutes.NextCommand);
        using var pollResponse = await host.Client.SendAsync(pollRequest);
        var document = JsonDocument.Parse(await pollResponse.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, pollResponse.StatusCode);
        Assert.Equal("submitMarketOrder",
            document.RootElement.GetProperty("commandType").GetString());
        var payload = document.RootElement.GetProperty("command");
        Assert.Equal(command.CommandId,
            payload.GetProperty("commandId").GetGuid());
        Assert.Equal(command.ClientCorrelationId,
            payload.GetProperty("clientCorrelationId").GetString());
        Assert.Equal(PositionOwnership.DefaultOwnershipTag,
            payload.GetProperty("ownershipTag").GetString());

        var acknowledgement = Acknowledgement(host, command.CommandId);
        using var acknowledgementRequest = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.ExecutionAcknowledgement, acknowledgement);
        using var acknowledgementResponse = await host.Client.SendAsync(acknowledgementRequest);
        var result = await sendTask;

        Assert.Equal(HttpStatusCode.NoContent, acknowledgementResponse.StatusCode);
        Assert.Equal(OrderStatus.Accepted, result.Result.Status);
        Assert.Equal("broker-position-1", result.Result.BrokerPositionId);
        Assert.Equal(0, host.Transport.PendingCount);
    }

    [Fact]
    public async Task WrongBridgeCannotConsumeQueuedCommand()
    {
        await using var host = await ExecutableHostAsync();
        var command = Command(host);
        var sendTask = host.Transport.SendAsync(command);
        using var wrongRequest = host.SignedRequest(HttpMethod.Get,
            MT5BridgeRoutes.NextCommand, bridgeInstanceId: "wrong-bridge");

        using var wrongResponse = await host.Client.SendAsync(wrongRequest);
        using var correctRequest = host.SignedRequest(HttpMethod.Get,
            MT5BridgeRoutes.NextCommand);
        using var correctResponse = await host.Client.SendAsync(correctRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, correctResponse.StatusCode);
        await AcknowledgeAsync(host, command.CommandId);
        Assert.Equal(OrderStatus.Accepted, (await sendTask).Result.Status);
    }

    [Fact]
    public async Task CommandCannotBePolledByTwoConsumers()
    {
        await using var host = await ExecutableHostAsync();
        var command = Command(host);
        var sendTask = host.Transport.SendAsync(command);

        using var firstRequest = host.SignedRequest(HttpMethod.Get,
            MT5BridgeRoutes.NextCommand);
        using var first = await host.Client.SendAsync(firstRequest);
        using var secondRequest = host.SignedRequest(HttpMethod.Get,
            MT5BridgeRoutes.NextCommand);
        using var second = await host.Client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        await AcknowledgeAsync(host, command.CommandId);
        await sendTask;
    }

    [Fact]
    public async Task WrongCommandIdCannotCompleteAnotherCommand()
    {
        await using var host = await ExecutableHostAsync();
        var command = Command(host);
        var sendTask = host.Transport.SendAsync(command);
        await PollAsync(host);
        using var wrongRequest = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.ExecutionAcknowledgement,
            Acknowledgement(host, Guid.NewGuid()));

        using var wrongResponse = await host.Client.SendAsync(wrongRequest);

        Assert.Equal(HttpStatusCode.Conflict, wrongResponse.StatusCode);
        Assert.False(sendTask.IsCompleted);
        await AcknowledgeAsync(host, command.CommandId);
        Assert.Equal(command.CommandId, (await sendTask).CommandId);
    }

    [Fact]
    public async Task DuplicateAcknowledgementIsRejected()
    {
        await using var host = await ExecutableHostAsync();
        var command = Command(host);
        var sendTask = host.Transport.SendAsync(command);
        await PollAsync(host);
        var acknowledgement = Acknowledgement(host, command.CommandId);
        await AcknowledgeAsync(host, acknowledgement);
        await sendTask;
        using var duplicateRequest = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.ExecutionAcknowledgement, acknowledgement);

        using var duplicate = await host.Client.SendAsync(duplicateRequest);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task TimeoutBeforeDeliveryRejectsAndCommandCannotLaterBePolled()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true,
            CommandTimeout = TimeSpan.FromMilliseconds(100)
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        await host.PrepareReadyStateAsync();

        var acknowledgement = await host.Transport.SendAsync(Command(host));

        Assert.Equal(OrderStatus.Rejected, acknowledgement.Result.Status);
        Assert.Contains("timed out before delivery", acknowledgement.Result.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.Transport.PendingCount);
        Assert.False(TryPoll(host, out _));
    }

    [Fact]
    public async Task CancellationBeforeDeliveryPropagatesAndCannotLaterBePolled()
    {
        await using var host = await ExecutableHostAsync();
        using var cancellation = new CancellationTokenSource();
        var sendTask = host.Transport.SendAsync(Command(host), cancellation.Token);
        Assert.Equal(1, host.Transport.PendingCount);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        Assert.Equal(0, host.Transport.PendingCount);
        Assert.False(TryPoll(host, out _));
    }

    [Fact]
    public async Task CancellationAfterDeliveryReturnsIndeterminateAndReleasesCapacity()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true,
            PendingCommandCapacity = 1,
            CommandTimeout = TimeSpan.FromSeconds(5)
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        await host.PrepareReadyStateAsync();
        using var cancellation = new CancellationTokenSource();
        var sendTask = host.Transport.SendAsync(Command(host), cancellation.Token);
        Assert.True(TryPoll(host, out _));

        cancellation.Cancel();
        var result = await sendTask;

        Assert.Equal(OrderStatus.Indeterminate, result.Result.Status);
        Assert.Contains("cancelled after delivery", result.Result.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broker outcome may be unknown", result.Result.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.Transport.PendingCount);

        var next = Command(host);
        var nextTask = host.Transport.SendAsync(next);
        Assert.True(TryPoll(host, out var polled));
        Assert.Equal(next.CommandId, polled!.CommandId);
        Assert.Equal(MT5AcknowledgementDecision.Accepted,
            host.Transport.AcceptAcknowledgement(Acknowledgement(host, next.CommandId)));
        Assert.Equal(OrderStatus.Accepted, (await nextTask).Result.Status);
    }

    [Fact]
    public async Task TimeoutAfterDeliveryReturnsIndeterminate()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true,
            CommandTimeout = TimeSpan.FromMilliseconds(100)
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        await host.PrepareReadyStateAsync();
        var command = Command(host);
        var sendTask = host.Transport.SendAsync(command);
        Assert.True(TryPoll(host, out var polled));
        Assert.Equal(command.CommandId, polled!.CommandId);

        var result = await sendTask;

        Assert.Equal(OrderStatus.Indeterminate, result.Result.Status);
        Assert.Contains("timed out after delivery", result.Result.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broker outcome is unknown", result.Result.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.Transport.PendingCount);
    }

    [Fact]
    public async Task PendingCommandStorageIsBounded()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true,
            PendingCommandCapacity = 1,
            CommandTimeout = TimeSpan.FromSeconds(5)
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        await host.PrepareReadyStateAsync();
        var first = Command(host);
        var firstTask = host.Transport.SendAsync(first);

        var rejected = await host.Transport.SendAsync(Command(host));

        Assert.Equal(OrderStatus.Rejected, rejected.Result.Status);
        Assert.Equal(1, host.Transport.PendingCount);
        await PollAsync(host);
        await AcknowledgeAsync(host, first.CommandId);
        await firstTask;
        Assert.Equal(0, host.Transport.PendingCount);
    }

    [Fact]
    public async Task LateAcknowledgementForIndeterminateCommandCannotCompleteAnotherCommand()
    {
        await using var host = await ExecutableHostAsync();
        using var cancellation = new CancellationTokenSource();
        var first = Command(host);
        var firstTask = host.Transport.SendAsync(first, cancellation.Token);
        Assert.True(TryPoll(host, out _));
        cancellation.Cancel();
        Assert.Equal(OrderStatus.Indeterminate, (await firstTask).Result.Status);

        var second = Command(host);
        var secondTask = host.Transport.SendAsync(second);
        var lateDecision = host.Transport.AcceptAcknowledgement(
            Acknowledgement(host, first.CommandId));

        Assert.Equal(MT5AcknowledgementDecision.IndeterminateCommand, lateDecision);
        Assert.False(secondTask.IsCompleted);
        using var lateRequest = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.ExecutionAcknowledgement,
            Acknowledgement(host, first.CommandId));
        using var lateResponse = await host.Client.SendAsync(lateRequest);
        var lateBody = JsonDocument.Parse(
            await lateResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Conflict, lateResponse.StatusCode);
        Assert.Equal("acknowledgement_indeterminate",
            lateBody.RootElement.GetProperty("code").GetString());
        Assert.False(secondTask.IsCompleted);
        Assert.True(TryPoll(host, out var polled));
        Assert.Equal(second.CommandId, polled!.CommandId);
        Assert.Equal(MT5AcknowledgementDecision.Accepted,
            host.Transport.AcceptAcknowledgement(Acknowledgement(host, second.CommandId)));
        Assert.Equal(second.CommandId, (await secondTask).CommandId);
        Assert.Equal(MT5AcknowledgementDecision.UnknownCommand,
            host.Transport.AcceptAcknowledgement(Acknowledgement(host, Guid.NewGuid())));
    }

    [Fact]
    public async Task LateAcknowledgementAfterDeliveredTimeoutIsClassifiedIndeterminate()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true,
            CommandTimeout = TimeSpan.FromMilliseconds(100)
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        await host.PrepareReadyStateAsync();
        var command = Command(host);
        var sendTask = host.Transport.SendAsync(command);
        Assert.True(TryPoll(host, out _));
        Assert.Equal(OrderStatus.Indeterminate, (await sendTask).Result.Status);

        var decision = host.Transport.AcceptAcknowledgement(
            Acknowledgement(host, command.CommandId));

        Assert.Equal(MT5AcknowledgementDecision.IndeterminateCommand, decision);
    }

    [Fact]
    public async Task CancelledStaleCommandIdIsSkippedWhenNextCommandIsPolled()
    {
        await using var host = await ExecutableHostAsync();
        using var cancellation = new CancellationTokenSource();
        var stale = Command(host);
        var staleTask = host.Transport.SendAsync(stale, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleTask);

        var current = Command(host);
        var currentTask = host.Transport.SendAsync(current);

        Assert.True(TryPoll(host, out var polled));
        Assert.Equal(current.CommandId, polled!.CommandId);
        Assert.NotEqual(stale.CommandId, polled.CommandId);
        Assert.Equal(MT5AcknowledgementDecision.Accepted,
            host.Transport.AcceptAcknowledgement(Acknowledgement(host, current.CommandId)));
        await currentTask;
    }

    [Fact]
    public async Task RepeatedCancellationAndTimeoutDoNotExhaustBoundedQueue()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true,
            PendingCommandCapacity = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(50)
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        await host.PrepareReadyStateAsync();

        for (var index = 0; index < 3; index++)
        {
            using var cancellation = new CancellationTokenSource();
            var cancelled = host.Transport.SendAsync(Command(host), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            var timedOut = await host.Transport.SendAsync(Command(host));
            Assert.Equal(OrderStatus.Rejected, timedOut.Result.Status);
        }

        var final = Command(host);
        var finalTask = host.Transport.SendAsync(final);
        Assert.True(TryPoll(host, out var polled));
        Assert.Equal(final.CommandId, polled!.CommandId);
        Assert.Equal(MT5AcknowledgementDecision.Accepted,
            host.Transport.AcceptAcknowledgement(Acknowledgement(host, final.CommandId)));
        Assert.Equal(OrderStatus.Accepted, (await finalTask).Result.Status);
        Assert.Equal(0, host.Transport.PendingCount);
    }

    [Fact]
    public async Task UnauthenticatedSessionCannotQueueCommand()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        ApplyReadyStateDirectly(host);

        var result = await host.Transport.SendAsync(Command(host));

        Assert.Equal(OrderStatus.Rejected, result.Result.Status);
        Assert.Equal(0, host.Transport.PendingCount);
    }

    [Fact]
    public async Task StaleExecutionSnapshotCannotBePolled()
    {
        await using var host = await ExecutableHostAsync();
        using var cancellation = new CancellationTokenSource();
        var sendTask = host.Transport.SendAsync(Command(host), cancellation.Token);
        host.Clock.Advance(TimeSpan.FromSeconds(16));
        using var heartbeat = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(), true));
        using var heartbeatResponse = await host.Client.SendAsync(heartbeat);

        using var poll = host.SignedRequest(HttpMethod.Get, MT5BridgeRoutes.NextCommand);
        using var pollResponse = await host.Client.SendAsync(poll);

        Assert.Equal(HttpStatusCode.NoContent, heartbeatResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, pollResponse.StatusCode);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
    }

    private static async Task<MT5BridgeHostTestFixture> ExecutableHostAsync()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true,
            CommandTimeout = TimeSpan.FromSeconds(5)
        };
        var host = await MT5BridgeHostTestFixture.StartAsync(options);
        await host.PrepareReadyStateAsync();
        return host;
    }

    private static MT5SubmitMarketOrderCommand Command(MT5BridgeHostTestFixture host)
    {
        var signalId = Guid.NewGuid();
        return new(host.Envelope(), Guid.NewGuid(), host.Settings.Identity.AccountIdentifier,
            signalId, $"GoldAiTrader-{signalId:N}", "v1",
            PositionOwnership.DefaultOwnershipTag, "XAUUSD", "GOLD.test",
            TradeDirection.Buy, 1m, 2000m, 1999m, 2002m);
    }

    private static MT5ExecutionAcknowledgement Acknowledgement(
        MT5BridgeHostTestFixture host, Guid commandId) =>
        new(host.Envelope(), commandId, new(OrderStatus.Accepted,
            "Accepted by local test bridge.", "broker-order-1", 0m,
            "broker-position-1"));

    private static async Task PollAsync(MT5BridgeHostTestFixture host)
    {
        using var request = host.SignedRequest(HttpMethod.Get, MT5BridgeRoutes.NextCommand);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static bool TryPoll(MT5BridgeHostTestFixture host,
        out MT5ExecutionCommand? command) =>
        host.Transport.TryPollNext(host.Settings.Identity.BridgeInstanceId,
            host.Settings.Identity.ProtocolVersion, out command);

    private static Task AcknowledgeAsync(MT5BridgeHostTestFixture host, Guid commandId) =>
        AcknowledgeAsync(host, Acknowledgement(host, commandId));

    private static async Task AcknowledgeAsync(MT5BridgeHostTestFixture host,
        MT5ExecutionAcknowledgement acknowledgement)
    {
        using var request = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.ExecutionAcknowledgement, acknowledgement);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static void ApplyReadyStateDirectly(MT5BridgeHostTestFixture host)
    {
        var envelope = host.Envelope();
        host.State.AcceptHeartbeat(new(envelope, true));
        host.State.AcceptAccount(new(envelope, MT5BridgeHostTestFixture.Account()));
        host.State.AcceptSymbol(new(envelope, MT5BridgeHostTestFixture.Symbol()));
        host.State.AcceptPositions(new(envelope, host.Settings.Identity.AccountIdentifier, []));
    }
}
