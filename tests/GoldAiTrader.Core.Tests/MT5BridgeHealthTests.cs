using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.MT5.BridgeHost;

namespace GoldAiTrader.Core.Tests;

public sealed class MT5BridgeHealthTests
{
    [Fact]
    public async Task LivenessIsIndependentOfTerminalAndTelemetryState()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();

        var initial = await AssertHealthAsync(host, MT5BridgeHealthRoutes.Liveness,
            HttpStatusCode.OK);
        Assert.Equal("healthy", initial.Status);
        Assert.Equal("process_alive", initial.Reason);

        host.Clock.Advance(TimeSpan.FromHours(1));

        var stale = await AssertHealthAsync(host, MT5BridgeHealthRoutes.Liveness,
            HttpStatusCode.OK);
        Assert.Equal(initial, stale);
    }

    [Fact]
    public async Task ReadinessProgressesThroughEachMissingTelemetryCategory()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        var envelope = host.Envelope();

        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable,
            "terminal_disconnected");

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Connection,
            new MT5ConnectionStateMessage(envelope, true));
        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable, "heartbeat_missing");
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        envelope = host.Envelope();

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(envelope, true));
        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable, "account_unavailable");

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Account,
            new MT5AccountMessage(envelope, MT5BridgeHostTestFixture.Account()));
        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable, "symbol_unavailable");

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Symbol,
            new MT5SymbolMessage(envelope, MT5BridgeHostTestFixture.Symbol()));
        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable, "positions_unavailable");

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Positions,
            new MT5PositionInventoryMessage(envelope,
                MT5BridgeHostTestFixture.Identity().AccountIdentifier, []));

        var ready = await AssertHealthAsync(host, MT5BridgeHealthRoutes.Readiness,
            HttpStatusCode.OK);
        Assert.Equal("ready", ready.Status);
        Assert.Equal("ready", ready.Reason);
    }

    [Fact]
    public async Task ReadinessDoesNotRequireExternalExecutionAuthorization()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        Assert.False(host.Settings.Host.ExternalExecutionEnabled);

        await host.PrepareReadyStateAsync();

        var result = await AssertHealthAsync(host, MT5BridgeHealthRoutes.Readiness,
            HttpStatusCode.OK);
        Assert.Equal("ready", result.Status);
    }

    [Theory]
    [InlineData("heartbeat_stale")]
    [InlineData("account_unavailable")]
    [InlineData("symbol_unavailable")]
    [InlineData("positions_unavailable")]
    public async Task ReadinessFailsClosedWhenTrustedTelemetryBecomesStale(
        string expectedReason)
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.PrepareReadyStateAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(16));
        var envelope = host.Envelope();

        if (expectedReason != "heartbeat_stale")
            await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
                new MT5HeartbeatMessage(envelope, true));
        if (expectedReason != "account_unavailable" && expectedReason != "heartbeat_stale")
            await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Account,
                new MT5AccountMessage(envelope, MT5BridgeHostTestFixture.Account()));
        if (expectedReason != "symbol_unavailable" && expectedReason != "heartbeat_stale")
            await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Symbol,
                new MT5SymbolMessage(envelope, MT5BridgeHostTestFixture.Symbol()));
        if (expectedReason != "positions_unavailable" && expectedReason != "heartbeat_stale")
            await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Positions,
                new MT5PositionInventoryMessage(envelope,
                    MT5BridgeHostTestFixture.Identity().AccountIdentifier, []));

        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable, expectedReason);
    }

    [Fact]
    public async Task DisconnectAndReconnectRequireFreshCurrentSessionSnapshots()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.PrepareReadyStateAsync();

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Connection,
            new MT5ConnectionStateMessage(host.Envelope(), false));
        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable,
            "terminal_disconnected");

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var envelope = host.Envelope();
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Heartbeat,
            new MT5HeartbeatMessage(envelope, true));
        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable, "account_unavailable");

        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Account,
            new MT5AccountMessage(envelope, MT5BridgeHostTestFixture.Account()));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Symbol,
            new MT5SymbolMessage(envelope, MT5BridgeHostTestFixture.Symbol()));
        await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.Positions,
            new MT5PositionInventoryMessage(envelope,
                MT5BridgeHostTestFixture.Identity().AccountIdentifier, []));

        await AssertReasonAsync(host, HttpStatusCode.OK, "ready");
    }

    [Fact]
    public async Task HealthResponsesDoNotExposeSecretsOrIdentifiers()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.PrepareReadyStateAsync();

        using var response = await host.Client.GetAsync(MT5BridgeHealthRoutes.Readiness);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(MT5BridgeHostTestFixture.Secret, body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(host.Settings.Identity.AccountIdentifier, body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(host.Settings.Identity.BridgeInstanceId, body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(host.Settings.Identity.TerminalInstanceId, body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(host.Settings.Identity.BrokerName, body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthMetricsAreSubscribableThroughSystemDiagnosticsMetrics()
    {
        var values = new ConcurrentDictionary<string, ConcurrentBag<double>>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == MT5BridgeHealthMetrics.MeterName)
                activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            values.GetOrAdd(instrument.Name, _ => []).Add(measurement));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            values.GetOrAdd(instrument.Name, _ => []).Add(measurement));
        listener.Start();

        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.PrepareReadyStateAsync();
        listener.RecordObservableInstruments();

        Assert.Contains(1d, values["goldaitrader.mt5.terminal.connected"]);
        Assert.Contains(1d, values["goldaitrader.mt5.bridge.ready"]);
        Assert.Contains(0d, values["goldaitrader.mt5.heartbeat.age"]);
        Assert.Contains(1d, values["goldaitrader.mt5.heartbeat.fresh"]);

        host.Clock.Advance(TimeSpan.FromSeconds(16));
        values.Clear();
        listener.RecordObservableInstruments();

        Assert.Contains(0d, values["goldaitrader.mt5.bridge.ready"]);
        Assert.Contains(16d, values["goldaitrader.mt5.heartbeat.age"]);
        Assert.Contains(0d, values["goldaitrader.mt5.heartbeat.fresh"]);
    }
    [Fact]
    public async Task HealthCounterMetricsUseOnlyBoundedNonSensitiveTags()
    {
        var measurements = new ConcurrentBag<CapturedCounterMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == MT5BridgeHealthMetrics.MeterName &&
                instrument.Name is "goldaitrader.mt5.health.transitions" or
                    "goldaitrader.mt5.telemetry.freshness.failures")
                activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            measurements.Add(new(instrument.Name, measurement, tags.ToArray())));
        listener.Start();

        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        await host.PrepareReadyStateAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(16));
        await AssertReasonAsync(host, HttpStatusCode.ServiceUnavailable, "heartbeat_stale");

        var captured = measurements.ToArray();
        Assert.Contains(captured, metric => HasTag(metric, "transition", "connected"));
        Assert.Contains(captured, metric => HasTag(metric, "transition", "ready"));
        Assert.Contains(captured, metric => HasTag(metric, "transition", "not_ready"));
        Assert.Contains(captured, metric => HasTag(metric, "reason", "heartbeat_stale"));

        var sensitiveValues = new[]
        {
            MT5BridgeHostTestFixture.Secret,
            host.Settings.Identity.AccountIdentifier,
            host.Settings.Identity.TerminalInstanceId,
            host.Settings.Identity.BridgeInstanceId,
            host.Settings.Identity.BrokerName
        };
        foreach (var metric in captured)
        {
            Assert.Equal(1, metric.Value);
            var tag = Assert.Single(metric.Tags);
            var value = Assert.IsType<string>(tag.Value);
            if (metric.Instrument == "goldaitrader.mt5.health.transitions")
            {
                Assert.Equal("transition", tag.Key);
                Assert.Contains(value,
                    new[] { "connected", "disconnected", "ready", "not_ready" });
            }
            else
            {
                Assert.Equal("reason", tag.Key);
                Assert.Contains(value, new[]
                {
                    "terminal_disconnected", "heartbeat_missing", "heartbeat_stale",
                    "account_unavailable", "symbol_unavailable", "positions_unavailable"
                });
            }

            var tagText = $"{tag.Key}={value}";
            foreach (var sensitiveValue in sensitiveValues)
                Assert.DoesNotContain(sensitiveValue, tagText, StringComparison.Ordinal);
            Assert.DoesNotContain("timestamp", tagText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nonce", tagText, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool HasTag(CapturedCounterMeasurement metric, string key, string value) =>
        metric.Tags.Count == 1 && metric.Tags[0].Key == key &&
        Equals(metric.Tags[0].Value, value);

    private static async Task AssertReasonAsync(MT5BridgeHostTestFixture host,
        HttpStatusCode expectedStatus, string expectedReason)
    {
        var result = await AssertHealthAsync(host, MT5BridgeHealthRoutes.Readiness,
            expectedStatus);
        Assert.Equal(expectedStatus == HttpStatusCode.OK ? "ready" : "not_ready",
            result.Status);
        Assert.Equal(expectedReason, result.Reason);
    }

    private static async Task<MT5BridgeHealthResponse> AssertHealthAsync(
        MT5BridgeHostTestFixture host, string path, HttpStatusCode expectedStatus)
    {
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(expectedStatus, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<MT5BridgeHealthResponse>(
            host.JsonOptions);
        return Assert.IsType<MT5BridgeHealthResponse>(result);
    }
    private sealed record CapturedCounterMeasurement(string Instrument, long Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
