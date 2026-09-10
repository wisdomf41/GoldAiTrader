using System.Net;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Core;
using GoldAiTrader.Execution;
using GoldAiTrader.MT5.BridgeHost;

namespace GoldAiTrader.Core.Tests;

public sealed class MT5BridgeHostIngestionTests
{
    [Fact]
    public async Task AuthenticatedRoutesIngestAllSupportedBridgeStateMessages()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(), true));
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var envelope = host.Envelope();

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Connection,
            new MT5ConnectionStateMessage(envelope, true, "connected"));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Account,
            new MT5AccountMessage(envelope, MT5BridgeHostTestFixture.Account()));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Symbol,
            new MT5SymbolMessage(envelope, MT5BridgeHostTestFixture.Symbol()));
        var position = Position();
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Positions,
            new MT5PositionInventoryMessage(envelope,
                host.Settings.Identity.AccountIdentifier, [position]));
        var timeframe = TimeSpan.FromMinutes(5);
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.CompletedBar,
            new MT5CompletedBarMessage(envelope, new("GOLD.test", "XAUUSD", timeframe,
                host.Clock.GetUtcNow() - timeframe, 2000m, 2002m, 1999m, 2001m,
                2000.9m, 2001m, 25m)));

        Assert.True(host.State.GetHealth().Healthy);
        Assert.Equal(AccountEnvironment.Demo, host.State.GetTrustedAccount().Environment);
        Assert.Equal("GOLD.test",
            host.State.GetTrustedSpecification("XAUUSD").BrokerSymbol);
        Assert.Equal(position.PositionId, Assert.Single(
            host.State.GetTrustedPositions()).PositionId);
        Assert.True(host.State.TryGetLatestCompletedBar("XAUUSD", timeframe, out var bar));
        Assert.Equal(2001m, bar!.Close);
    }

    [Fact]
    public async Task HttpIngestionPreservesBridgeStateOrderingAuthority()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.PrepareReadyStateAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Account,
            new MT5AccountMessage(host.Envelope(),
                MT5BridgeHostTestFixture.Account() with { Balance = 2000m }));
        var olderEnvelope = host.Envelope(
            host.Clock.GetUtcNow() - TimeSpan.FromSeconds(1));
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Account,
            new MT5AccountMessage(olderEnvelope,
                MT5BridgeHostTestFixture.Account() with { Balance = 1500m }));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(2000m, host.State.GetTrustedAccount().Balance);
    }

    [Fact]
    public async Task AuthenticatedDisconnectAndReconnectRequireFreshSessionSnapshots()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.PrepareReadyStateAsync();
        Assert.True(host.State.GetExecutionSnapshot().Ready);
        host.Clock.Advance(TimeSpan.FromSeconds(1));

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Connection,
            new MT5ConnectionStateMessage(host.Envelope(), false));

        Assert.False(host.State.GetHealth().Healthy);
        Assert.False(host.State.GetExecutionSnapshot().Ready);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(), true));
        Assert.True(host.State.GetHealth().Healthy);
        Assert.False(host.State.GetExecutionSnapshot().Ready);

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Account,
            new MT5AccountMessage(host.Envelope(), MT5BridgeHostTestFixture.Account()));
        Assert.False(host.State.GetExecutionSnapshot().Ready);
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Symbol,
            new MT5SymbolMessage(host.Envelope(), MT5BridgeHostTestFixture.Symbol()));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Positions,
            new MT5PositionInventoryMessage(host.Envelope(),
                host.Settings.Identity.AccountIdentifier, []));

        Assert.True(host.State.GetExecutionSnapshot().Ready);
    }

    [Fact]
    public async Task LiveAccountRemainsNonExecutableThroughHttpHost()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            ExternalExecutionEnabled = true
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(), true));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Account,
            new MT5AccountMessage(host.Envelope(),
                MT5BridgeHostTestFixture.Account(AccountEnvironment.Live)));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Symbol,
            new MT5SymbolMessage(host.Envelope(), MT5BridgeHostTestFixture.Symbol()));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Positions,
            new MT5PositionInventoryMessage(host.Envelope(),
                host.Settings.Identity.AccountIdentifier, []));
        var gateway = new MT5ExecutionGateway(host.State, host.Transport, host.Clock);

        var executionAccount = await gateway.GetExecutionAccountAsync(default);

        Assert.Equal(AccountEnvironment.Live, executionAccount.Environment);
        Assert.False(executionAccount.IsVerifiedDemo);
    }

    private static MT5PositionDescription Position()
    {
        var signalId = Guid.NewGuid();
        return new("position-http-1", "GOLD.test", "XAUUSD", TradeDirection.Buy,
            1m, 2000m, 1999m, 2002m, 1m, signalId,
            $"GoldAiTrader-{signalId:N}", "v1", PositionOwnership.DefaultOwnershipTag);
    }
}
