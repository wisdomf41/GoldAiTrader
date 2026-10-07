using System.Diagnostics.Metrics;
using GoldAiTrader.Adapters.MT5;

namespace GoldAiTrader.MT5.BridgeHost;

public static class MT5BridgeHealthRoutes
{
    public const string Liveness = "/health/live";
    public const string Readiness = "/health/ready";
}

public sealed record MT5BridgeHealthResponse(string Status, string Reason);

public sealed class MT5BridgeHealthMetrics : IDisposable
{
    public const string MeterName = "GoldAiTrader.MT5.BridgeHost";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> transitions;
    private readonly Counter<long> telemetryFreshnessFailures;
    private readonly MT5BridgeState state;

    public MT5BridgeHealthMetrics(MT5BridgeState state)
    {
        this.state = state;
        meter.CreateObservableGauge("goldaitrader.mt5.terminal.connected",
            () => state.GetReadinessSnapshot().TerminalConnected ? 1L : 0L,
            description: "Whether the MT5 terminal is connected (1) or disconnected (0).");
        meter.CreateObservableGauge("goldaitrader.mt5.bridge.ready",
            () => state.GetReadinessSnapshot().Ready ? 1L : 0L,
            description: "Whether current-session MT5 telemetry is ready (1) or not ready (0).");
        meter.CreateObservableGauge("goldaitrader.mt5.heartbeat.age",
            () =>
            {
                var age = state.GetReadinessSnapshot().HeartbeatAge;
                return age is { } value && value >= TimeSpan.Zero
                    ? value.TotalSeconds
                    : -1d;
            },
            unit: "s",
            description: "Age of the last accepted MT5 heartbeat; -1 means unavailable.");
        meter.CreateObservableGauge("goldaitrader.mt5.heartbeat.fresh",
            () => state.GetReadinessSnapshot().HeartbeatFresh ? 1L : 0L,
            description: "Whether the last MT5 heartbeat is fresh (1) or unavailable/stale (0).");
        transitions = meter.CreateCounter<long>("goldaitrader.mt5.health.transitions",
            description: "Count of bounded MT5 connection and readiness transitions.");
        telemetryFreshnessFailures = meter.CreateCounter<long>(
            "goldaitrader.mt5.telemetry.freshness.failures",
            description: "Count of observed not-ready telemetry categories.");
    }

    internal void Record(MT5BridgeReadinessSnapshot current,
        MT5BridgeReadinessSnapshot? previous)
    {

        if (previous is null)
            return;

        if (previous.TerminalConnected != current.TerminalConnected)
            transitions.Add(1, new KeyValuePair<string, object?>("transition",
                current.TerminalConnected ? "connected" : "disconnected"));

        if (previous.Ready != current.Ready)
            transitions.Add(1, new KeyValuePair<string, object?>("transition",
                current.Ready ? "ready" : "not_ready"));

        if (!current.Ready && previous.Reason != current.Reason)
            telemetryFreshnessFailures.Add(1,
                new KeyValuePair<string, object?>("reason", current.Reason.ToWireValue()));
    }

    public void Dispose() => meter.Dispose();
}

public sealed class MT5BridgeHealthMonitor
{
    private readonly object sync = new();
    private readonly MT5BridgeState state;
    private readonly MT5BridgeHealthMetrics metrics;
    private readonly ILogger<MT5BridgeHealthMonitor> logger;
    private MT5BridgeReadinessSnapshot previous;

    public MT5BridgeHealthMonitor(MT5BridgeState state, MT5BridgeHealthMetrics metrics,
        ILogger<MT5BridgeHealthMonitor> logger)
    {
        this.state = state;
        this.metrics = metrics;
        this.logger = logger;
        previous = state.GetReadinessSnapshot();
        metrics.Record(previous, null);
    }

    public MT5BridgeReadinessSnapshot Observe()
    {
        lock (sync)
        {
            var current = state.GetReadinessSnapshot();
            metrics.Record(current, previous);

            if (previous.TerminalConnected != current.TerminalConnected)
                logger.LogInformation(
                    "MT5 terminal connection state changed to {ConnectionState}.",
                    current.TerminalConnected ? "connected" : "disconnected");

            if (previous.Ready != current.Ready)
                logger.LogInformation(
                    "MT5 bridge readiness changed to {ReadinessState} ({Reason}).",
                    current.Ready ? "ready" : "not_ready", current.Reason.ToWireValue());

            previous = current;
            return current;
        }
    }
}

internal static class MT5BridgeReadinessReasonExtensions
{
    internal static string ToWireValue(this MT5BridgeReadinessReason reason) =>
        reason switch
        {
            MT5BridgeReadinessReason.Ready => "ready",
            MT5BridgeReadinessReason.TerminalDisconnected => "terminal_disconnected",
            MT5BridgeReadinessReason.HeartbeatMissing => "heartbeat_missing",
            MT5BridgeReadinessReason.HeartbeatStale => "heartbeat_stale",
            MT5BridgeReadinessReason.AccountUnavailable => "account_unavailable",
            MT5BridgeReadinessReason.SymbolUnavailable => "symbol_unavailable",
            MT5BridgeReadinessReason.PositionsUnavailable => "positions_unavailable",
            _ => "internal_unhealthy"
        };
}
