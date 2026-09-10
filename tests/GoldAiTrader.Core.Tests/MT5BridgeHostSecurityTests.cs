using System.Net;
using System.Text;
using System.Text.Json;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.MT5.BridgeHost;

namespace GoldAiTrader.Core.Tests;

public sealed class MT5BridgeHostSecurityTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("localhost")]
    [InlineData("[::]")]
    [InlineData("192.168.1.10")]
    public void HostConfigurationRejectsNonExplicitIpv4Loopback(string address)
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with { BindAddress = address };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void HostConfigurationAcceptsExactIpv4Loopback()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions();

        options.Validate();

        Assert.Equal("127.0.0.1", options.Endpoint.Address.Host);
    }

    [Fact]
    public void MissingSecretFailsClosed()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with { SharedSecret = null };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BadSignatureIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(), true));
        request.Headers.Remove(MT5BridgeAuthenticationHeaders.Signature);
        request.Headers.TryAddWithoutValidation(MT5BridgeAuthenticationHeaders.Signature,
            new string('0', 64));

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("signature_invalid", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task MalformedSignatureIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(), true));
        request.Headers.Remove(MT5BridgeAuthenticationHeaders.Signature);
        request.Headers.TryAddWithoutValidation(MT5BridgeAuthenticationHeaders.Signature,
            "not-hex");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("signature_malformed", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ModifiedBodyInvalidatesSignatureBeforeStateMutation()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var original = host.Serialize(new MT5HeartbeatMessage(host.Envelope(), true));
        var modified = host.Serialize(new MT5HeartbeatMessage(host.Envelope(), false));
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            signingBody: original, transmittedBody: modified);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(host.State.GetHealth().Healthy);
    }

    [Fact]
    public async Task ModifiedPathInvalidatesSignature()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Connection,
            new MT5ConnectionStateMessage(host.Envelope(), true),
            signedPath: MT5BridgeRoutes.Heartbeat);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("signature_invalid", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ModifiedMethodInvalidatesSignature()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(), true), signedMethod: HttpMethod.Get);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("signature_invalid", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task StaleSignedRequestIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var stale = host.Clock.GetUtcNow() -
            host.Settings.Host.AuthenticationTolerance - TimeSpan.FromSeconds(1);
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(stale), true), timestamp: stale);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("timestamp_outside_window", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task FutureSignedRequestOutsideToleranceIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var future = host.Clock.GetUtcNow() +
            host.Settings.Host.AuthenticationTolerance + TimeSpan.FromSeconds(1);
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(future), true), timestamp: future);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("timestamp_outside_window", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task DuplicateNonceIsRejectedByBoundedReplayCache()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        const string nonce = "duplicate-nonce-0000000000000001";
        var message = new MT5HeartbeatMessage(host.Envelope(), true);
        using var firstRequest = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.Heartbeat, message, nonce);
        using var secondRequest = host.SignedRequest(HttpMethod.Post,
            MT5BridgeRoutes.Heartbeat, message, nonce);

        using var first = await host.Client.SendAsync(firstRequest);
        using var second = await host.Client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("request_replayed", await ErrorCodeAsync(second));
        Assert.Equal(1, host.ReplayCache.Count);
    }

    [Fact]
    public async Task OversizedRequestIsRejectedBeforeDeserializationOrMutation()
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            MaximumRequestBodyBytes = 1024
        };
        await using var host = await MT5BridgeHostTestFixture.StartAsync(options);
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new { padding = new string('x', 2048) });

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(host.State.GetHealth().Healthy);
        Assert.Equal(0, host.ReplayCache.Count);
    }

    [Fact]
    public async Task MalformedJsonIsRejectedWithoutStateMutation()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var malformed = Encoding.UTF8.GetBytes("{\"envelope\":");
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            signingBody: malformed);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("json_invalid", await ErrorCodeAsync(response));
        Assert.False(host.State.GetHealth().Healthy);
    }

    [Fact]
    public async Task UnknownJsonMemberIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            envelope = host.Envelope(),
            terminalConnected = true,
            unexpected = "rejected"
        }, host.JsonOptions);
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            signingBody: json);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("json_invalid", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task BodyProtocolMismatchIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var message = new MT5HeartbeatMessage(
            host.Envelope(protocolVersion: "99.0"), true);
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            message);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("protocol_version_invalid", await ErrorCodeAsync(response));
        Assert.False(host.State.GetHealth().Healthy);
        Assert.False(host.Transport.IsAuthenticated);
    }

    [Fact]
    public async Task WrongBridgeIdentityIsRejected()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(host.Envelope(), true),
            bridgeInstanceId: "wrong-bridge-instance");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("bridge_identity_invalid", await ErrorCodeAsync(response));
        Assert.False(host.State.GetHealth().Healthy);
    }

    [Fact]
    public async Task MissingAuthenticationDoesNotMutateBridgeState()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        using var content = new ByteArrayContent(host.Serialize(
            new MT5HeartbeatMessage(host.Envelope(), true)));
        content.Headers.ContentType = new("application/json");

        using var response = await host.Client.PostAsync(MT5BridgeRoutes.Heartbeat, content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(host.State.GetHealth().Healthy);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var error = JsonSerializer.Deserialize<MT5BridgeError>(
            await response.Content.ReadAsByteArrayAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return error?.Code;
    }
}
