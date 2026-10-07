using System.Net;
using System.Security.Cryptography;
using System.Text;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.MT5.BridgeHost;

namespace GoldAiTrader.Core.Tests;

public sealed class MT5TerminalBridgeContractTests
{
    [Fact]
    public async Task RepresentativeTerminalPayloadsAreAcceptedByStrictHostContract()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();

        await SendAcceptedAsync(host, MT5BridgeRoutes.Heartbeat, "heartbeat.json",
            "fixture-heartbeat-00000001");
        await SendAcceptedAsync(host, MT5BridgeRoutes.Account, "account.json",
            "fixture-account-000000001");
        await SendAcceptedAsync(host, MT5BridgeRoutes.Symbol, "symbol.json",
            "fixture-symbol-0000000001");
        await SendAcceptedAsync(host, MT5BridgeRoutes.Positions, "positions.json",
            "fixture-positions-0000001");
        await SendAcceptedAsync(host, MT5BridgeRoutes.CompletedBar, "completed-bar.json",
            "fixture-bar-0000000000001");

        Assert.True(host.State.GetHealth().Healthy);
        Assert.Equal("account-http-42", host.State.GetTrustedAccount().AccountIdentifier);
        Assert.Equal("GOLD.test",
            host.State.GetTrustedSpecification("XAUUSD").BrokerSymbol);
        Assert.Empty(host.State.GetTrustedPositions());
        Assert.True(host.State.TryGetLatestCompletedBar("XAUUSD",
            TimeSpan.FromMinutes(5), out var bar));
        Assert.Equal(2001m, bar!.Close);
    }

    [Fact]
    public async Task TerminalFixtureWithInvalidSignatureIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var body = Payload("heartbeat.json");
        var invalidSecret = (MT5BridgeHostTestFixture.Secret[0] == '0' ? "1" : "0") +
            MT5BridgeHostTestFixture.Secret[1..];
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            nonce: "fixture-invalid-signature-01",
            secret: invalidSecret,
            signingBody: body);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(host.State.GetHealth().Healthy);
    }

    [Fact]
    public async Task StaleTerminalFixtureIsRejectedBeforeIngestion()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var stale = host.Clock.GetUtcNow() - TimeSpan.FromSeconds(31);
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            nonce: "fixture-stale-timestamp-001", timestamp: stale,
            signingBody: Payload("heartbeat.json"));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(host.State.GetHealth().Healthy);
    }

    [Fact]
    public async Task ReplayedTerminalFixtureNonceIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var body = Payload("heartbeat.json");
        const string nonce = "fixture-replayed-nonce-0001";
        using var firstRequest = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.Heartbeat, nonce: nonce, signingBody: body);
        using var replayRequest = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.Heartbeat, nonce: nonce, signingBody: body);

        using var first = await host.Client.SendAsync(firstRequest);
        using var replay = await host.Client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task MalformedTerminalFixtureIsRejectedWithoutMutation()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            nonce: "fixture-malformed-json-0001", signingBody: Payload("malformed.json"));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(host.State.GetHealth().Healthy);
    }

    [Fact]
    public void SigningCanonicalizationUsesExactFixtureBytesAndLfSeparators()
    {
        var body = Payload("heartbeat.json");
        var canonical = Encoding.UTF8.GetString(MT5RequestSigner.CreateCanonicalPayload(
            "bridge-http-a", "1.0", "post", MT5BridgeRoutes.Heartbeat,
            "2026-09-08T10:00:00.0000000+00:00", "fixture-canonical-nonce-001", body));
        var lines = canonical.Split('\n');

        Assert.Equal(7, lines.Length);
        Assert.Equal("bridge-http-a", lines[0]);
        Assert.Equal("1.0", lines[1]);
        Assert.Equal("POST", lines[2]);
        Assert.Equal(MT5BridgeRoutes.Heartbeat, lines[3]);
        Assert.Equal("2026-09-08T10:00:00.0000000+00:00", lines[4]);
        Assert.Equal("fixture-canonical-nonce-001", lines[5]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), lines[6]);
        Assert.DoesNotContain('\r', canonical);
    }

    [Fact]
    public void TerminalSourceRemainsTelemetryOnlyAndLoopbackRestricted()
    {
        var root = RepositoryRoot();
        var bridgeRoot = Path.Combine(root, "platforms", "mt5", "GoldAiTraderBridge");
        var source = string.Join('\n', Directory.EnumerateFiles(bridgeRoot, "*.mq*",
            SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.Contains("http://127.0.0.1:", source, StringComparison.Ordinal);
        Assert.Contains("WebRequest(\"POST\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderSend", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CTrade", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/commands/next", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/execution/ack", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Print(m_secret", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Print(secret", source, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SendAcceptedAsync(MT5BridgeHostTestFixture host,
        string path, string fixture, string nonce)
    {
        using var request = host.SignedRequest(HttpMethod.Post, path,
            nonce: nonce, signingBody: Payload(fixture));
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static byte[] Payload(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "tests",
                "GoldAiTrader.Core.Tests", "Fixtures", "MT5Terminal", name);
            if (File.Exists(path))
                return Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"MT5 terminal fixture '{name}' was not found.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GoldAiTrader.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("GoldAiTrader repository root was not found.");
    }
}
