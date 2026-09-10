using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Core;
using GoldAiTrader.MT5.BridgeHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GoldAiTrader.Core.Tests;

internal sealed class MT5BridgeHostTestFixture : IAsyncDisposable
{
    internal const string Secret = "test-only-secret-value-32-bytes-minimum";
    internal static readonly DateTimeOffset InitialTime =
        DateTimeOffset.Parse("2026-09-08T10:00:00Z");

    private MT5BridgeHostTestFixture(WebApplication application,
        ManualBridgeTimeProvider clock, MT5BridgeHostSettings settings)
    {
        Application = application;
        Clock = clock;
        Settings = settings;
        Client = application.GetTestClient();
        JsonOptions = application.Services.GetRequiredService<JsonSerializerOptions>();
    }

    internal WebApplication Application { get; }
    internal HttpClient Client { get; }
    internal ManualBridgeTimeProvider Clock { get; }
    internal MT5BridgeHostSettings Settings { get; }
    internal JsonSerializerOptions JsonOptions { get; }
    internal MT5BridgeState State =>
        Application.Services.GetRequiredService<MT5BridgeState>();
    internal MT5PollingExecutionTransport Transport =>
        Application.Services.GetRequiredService<MT5PollingExecutionTransport>();
    internal MT5RequestReplayCache ReplayCache =>
        Application.Services.GetRequiredService<MT5RequestReplayCache>();

    internal static async Task<MT5BridgeHostTestFixture> StartAsync(
        MT5BridgeHostOptions? options = null)
    {
        var clock = new ManualBridgeTimeProvider(InitialTime);
        var hostOptions = options ?? ValidOptions();
        var settings = new MT5BridgeHostSettings(hostOptions, Identity(), Mappings());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(MT5BridgeHostApplication).Assembly.FullName
        });
        builder.WebHost.UseTestServer();
        var application = MT5BridgeHostApplication.Build(builder, settings, clock);
        await application.StartAsync();
        return new(application, clock, settings);
    }

    internal HttpRequestMessage SignedRequest(HttpMethod method, string path,
        object? body = null, string? nonce = null, DateTimeOffset? timestamp = null,
        string? secret = null, string? bridgeInstanceId = null,
        string? protocolVersion = null, HttpMethod? signedMethod = null,
        string? signedPath = null, byte[]? signingBody = null,
        byte[]? transmittedBody = null)
    {
        var bodyBytes = signingBody ?? (body is null ? [] : Serialize(body));
        var sentBytes = transmittedBody ?? bodyBytes;
        var timestampText = (timestamp ?? Clock.GetUtcNow()).ToUniversalTime().ToString("O");
        var requestNonce = nonce ?? Guid.NewGuid().ToString("N");
        var bridgeId = bridgeInstanceId ?? Settings.Identity.BridgeInstanceId;
        var version = protocolVersion ?? Settings.Identity.ProtocolVersion;
        var signature = MT5RequestSigner.Sign(secret ?? Settings.Host.SharedSecret!, bridgeId,
            version, (signedMethod ?? method).Method, signedPath ?? path,
            timestampText, requestNonce, bodyBytes);

        var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get || sentBytes.Length > 0)
        {
            request.Content = new ByteArrayContent(sentBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        request.Headers.TryAddWithoutValidation(
            MT5BridgeAuthenticationHeaders.BridgeInstanceId, bridgeId);
        request.Headers.TryAddWithoutValidation(
            MT5BridgeAuthenticationHeaders.ProtocolVersion, version);
        request.Headers.TryAddWithoutValidation(
            MT5BridgeAuthenticationHeaders.Timestamp, timestampText);
        request.Headers.TryAddWithoutValidation(MT5BridgeAuthenticationHeaders.Nonce,
            requestNonce);
        request.Headers.TryAddWithoutValidation(MT5BridgeAuthenticationHeaders.Signature,
            signature);
        return request;
    }

    internal byte[] Serialize(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), JsonOptions);

    internal async Task PrepareReadyStateAsync()
    {
        var envelope = Envelope();
        await SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(envelope, true));
        await SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Account,
            new MT5AccountMessage(envelope, Account()));
        await SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Symbol,
            new MT5SymbolMessage(envelope, Symbol()));
        await SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Positions,
            new MT5PositionInventoryMessage(envelope, Identity().AccountIdentifier, []));
    }

    internal async Task SendAcceptedAsync(HttpMethod method, string path, object body)
    {
        using var request = SignedRequest(method, path, body);
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    internal MT5MessageEnvelope Envelope(DateTimeOffset? sentAtUtc = null,
        string? protocolVersion = null, string? bridgeInstanceId = null) =>
        new(protocolVersion ?? Settings.Identity.ProtocolVersion,
            bridgeInstanceId ?? Settings.Identity.BridgeInstanceId,
            (sentAtUtc ?? Clock.GetUtcNow()).ToUniversalTime());

    internal static MT5BridgeHostOptions ValidOptions() => new()
    {
        SharedSecret = Secret,
        ExternalExecutionEnabled = false
    };

    internal static MT5BridgeIdentity Identity() =>
        new("bridge-http-a", "terminal-http-a", "Test Broker", "account-http-42",
            "1.0.0", MT5BridgeProtocol.CurrentVersion);

    internal static IReadOnlyDictionary<string, string> Mappings() =>
        new Dictionary<string, string> { ["XAUUSD"] = "GOLD.test" };

    internal static MT5AccountDescription Account(
        AccountEnvironment environment = AccountEnvironment.Demo) =>
        new("account-http-42", "USD", 1000m, 1000m, 1000m, 1000m,
            1000m, 1000m, 0, 0m, 900m, environment);

    internal static MT5SymbolDescription Symbol() =>
        new("GOLD.test", "XAUUSD", 0.01m, 1m, 100m,
            0.01m, 100m, 0.01m, 0.1m, true);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Application.DisposeAsync();
    }
}

internal sealed class ManualBridgeTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration) => utcNow += duration;
}
