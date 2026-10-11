# MT5 authenticated loopback HTTP bridge

## Boundary

`GoldAiTrader.MT5.BridgeHost` is the HTTP/JSON boundary between the chart-attached telemetry-only
MetaTrader 5 EA and `GoldAiTrader.Adapters.MT5`. It is a separate ASP.NET Core process,
keeping hosting, authentication, replay defence, parsing, and limits out of the adapter.

```text
GoldAiTraderBridge.mq5
            | WebRequest
        HTTP / JSON
            |
       127.0.0.1
            |
GoldAiTrader.MT5.BridgeHost
            |
  GoldAiTrader.Adapters.MT5
            |
 Universal Platform Gateway
            |
     GoldAiTrader Engine
```

This is transport, not strategy or risk authority. The repository now includes the MQL5 telemetry
publisher under `platforms/mt5/GoldAiTraderBridge`; it includes no command polling, broker order
submission, PostgreSQL/cTrader dependency, credential, or real order path.

## Binding and configuration

The listener binds to exact IPv4 `127.0.0.1`; startup rejects `localhost`, `::1`, `0.0.0.0`,
LAN/public addresses, and every other value. Port defaults to `5088`. There is no CORS,
Swagger, forwarded-header trust, or public fallback. Kestrel hides its server header.

Configuration is under `MT5Bridge`. Bridge, terminal, broker, account, and at least one symbol
mapping are required. The secret comes first from `GOLDAITRADER_MT5_BRIDGE_SECRET`, with
`MT5Bridge:SharedSecret` available for an external provider. There is no default; startup
fails when its UTF-8 value is missing or shorter than 32 bytes. Never commit or log it.

Validated defaults are: external execution `false`; authentication tolerance 30 seconds;
body limit 256 KiB; JSON depth 32; replay cache 4,096; pending commands 128; command timeout
30 seconds. Enable execution only in a separately reviewed Demo deployment.

## Routes

Bridge protocol routes use `/bridge/mt5/v1`. POST requires `application/json`; queries are rejected.

| Method | Route | Contract |
| --- | --- | --- |
| GET | `/health/live` | Process liveness; independent of MT5 state |
| GET | `/health/ready` | Current-session telemetry readiness; `200` or `503` |
| POST | `/heartbeat` | `MT5HeartbeatMessage` |
| POST | `/connection` | `MT5ConnectionStateMessage` |
| POST | `/account` | `MT5AccountMessage` |
| POST | `/symbol` | `MT5SymbolMessage` |
| POST | `/positions` | `MT5PositionInventoryMessage` |
| POST | `/bars/completed` | `MT5CompletedBarMessage` |
| POST | `/execution/ack` | `MT5ExecutionAcknowledgement` |
| GET | `/commands/next` | `204` or one claimed command |


## Health and observability contract

The two absolute health routes are unauthenticated so local diagnostics or probe tooling can
query them.
They remain loopback-only and return only fixed status/category values—never credentials,
identifiers, broker details, telemetry values, request material, or exception text.

`GET /health/live` returns `200` whenever the host process can serve requests. It is independent
of terminal connectivity and telemetry freshness.

`GET /health/ready` returns `200` only when the terminal is connected and the heartbeat,
configured canonical symbol specifications, account snapshot, and full position inventory are
fresh for the current session. It returns `503` with one bounded reason otherwise:
`terminal_disconnected`, `heartbeat_missing`, `heartbeat_stale`, `account_unavailable`,
`symbol_unavailable`, or `positions_unavailable`. Readiness does not enable execution and is
valid while `MT5Bridge__ExternalExecutionEnabled=false`.

The host emits logs only when connection or readiness state changes. Built-in
`System.Diagnostics.Metrics` instruments use meter `GoldAiTrader.MT5.BridgeHost` and expose
connected/readiness gauges, heartbeat age, bounded transition counts, and telemetry freshness
failure counts. A hosted supervisor observes the same health monitor at startup and every second,
including when no telemetry or readiness requests arrive. Set
`MT5Bridge:HealthSupervisionIntervalSeconds` (environment variable
`MT5Bridge__HealthSupervisionIntervalSeconds`) to a value from 0.1 through 30 seconds.
Startup rejects intervals outside that range. Detection latency is up to one interval plus
scheduler delay. Repeated observations of unchanged state do not add transition logs or counters.
Shutdown cancels the timer wait and disposes the timer.

### OTLP export and Prometheus boundary

```text
BridgeHost -> OTLP -> OpenTelemetry Collector -> Prometheus -> Grafana
```

BridgeHost exports only meter `GoldAiTrader.MT5.BridgeHost` using stable
`OpenTelemetry.Exporter.OpenTelemetryProtocol` and `OpenTelemetry.Extensions.Hosting`
packages pinned to `1.19.1`. It has no direct `/metrics` endpoint. The existing
`/health/live` and `/health/ready` routes remain on the authenticated bridge host's
loopback listener with their existing probe semantics. OTLP export is outbound and does
not open another BridgeHost listener, register trace/log export, or grant execution authority.

The metrics-only `AddOtlpExporter` registration reads standard configuration keys through
ASP.NET Core configuration, including environment variables:

| Variable | Meaning |
| --- | --- |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (default) or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Base Collector URL; default `http://localhost:4317` for gRPC, `http://localhost:4318` for HTTP/protobuf |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | Export request timeout in milliseconds |
| `OTEL_EXPORTER_OTLP_HEADERS` | Optional transport authentication headers; inject securely, never commit or log values |
| `OTEL_METRIC_EXPORT_INTERVAL` | Collection/export interval in milliseconds; SDK default 60000 |
| `OTEL_METRIC_EXPORT_TIMEOUT` | Reader export timeout in milliseconds |
| `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE` | Keep `cumulative` for this Prometheus pipeline |
| `OTEL_EXPORTER_OTLP_CERTIFICATE` | Trusted CA certificate path for TLS |
| `OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE`, `OTEL_EXPORTER_OTLP_CLIENT_KEY` | Mounted client certificate/key paths for mTLS |

With this registration, use the general `OTEL_EXPORTER_OTLP_*` endpoint/protocol/header/timeout
keys above, not their signal-specific `OTEL_EXPORTER_OTLP_METRICS_*` variants. The latter
transport overrides require a different SDK registration. For HTTP/protobuf, the base
environment endpoint receives the SDK's `/v1/metrics` suffix. Do not append it twice.

Example environment for a same-machine Collector:

```text
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
OTEL_EXPORTER_OTLP_TIMEOUT=5000
OTEL_METRIC_EXPORT_INTERVAL=10000
OTEL_METRIC_EXPORT_TIMEOUT=5000
OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=cumulative
```

The [Collector configuration](../deploy/observability/otel-collector.yaml) targets the core
Collector distribution `0.162.0`. It enables OTLP gRPC (4317), OTLP HTTP/protobuf (4318),
memory limiting, bounded batches, and Prometheus export (8889). All three listeners default
to IPv4 loopback. Validate and run with the pinned Collector binary:

```text
otelcol validate --config=deploy/observability/otel-collector.yaml
otelcol --config=deploy/observability/otel-collector.yaml
```

Prometheus scrapes the **Collector's** `http://127.0.0.1:8889/metrics`, never BridgeHost port
5088. The [Prometheus configuration](../deploy/observability/prometheus.yaml) demonstrates
a 15-second scrape interval. Point Grafana's Prometheus data source at the private Prometheus
service. Metric names use underscores, counters end in `_total`, and heartbeat age uses
`_seconds`.

### Deployment and operational controls

- For Docker, mount the Collector YAML read-only into the pinned
  `otel/opentelemetry-collector:0.162.0` image. Set `MT5_OTLP_GRPC_BIND=0.0.0.0:4317`,
  `MT5_OTLP_HTTP_BIND=0.0.0.0:4318`, and `MT5_PROMETHEUS_BIND=0.0.0.0:8889` **inside
  the Collector container only**. Restrict published ports to host loopback, or keep them
  entirely on a private container network. Use `http://collector:4318` only when
  BridgeHost shares that private network, and `collector:8889` as Prometheus's scrape target.
- For Kubernetes, mount this configuration through a ConfigMap and expose Collector ports
  through internal Services only. Use NetworkPolicies to allow OTLP from BridgeHost and
  port 8889 from Prometheus. A same-pod Collector may receive OTLP on loopback; a central
  Collector needs a private receiver binding and Service DNS. Do not change BridgeHost's
  loopback binding to accommodate monitoring.
- Plain HTTP is for local or isolated trusted networking. Across hosts, configure Collector
  receiver TLS/authentication and exporter HTTPS/mTLS; protect the scrape endpoint as well.
  Mount certificates and keys through the deployment's secret store. Never copy bridge
  credentials into metric attributes or reuse bridge HMAC headers for OTLP.
- Keep Collector memory above its 128 MiB limiter with runtime headroom; set container
  resource requests/limits and tune batching against measured load. The configuration is
  an example, not a provisioned deployment.
- Bridge gauges are sampled at export time; Collector scrapes show the latest received
  values, not an on-demand MT5 evaluation. Budget export, batch, and scrape intervals in
  addition to supervisor detection latency. Use `/health/ready` for immediate fail-closed
  decisions. Export/Collector outages do not change readiness or authorize execution.
- Alert separately on scrape failure, missing MT5 series, and Collector receive/export
  failures. The Collector expires series after two minutes without updates; until then,
  it can serve the last received ready value. Scrape `up=1` alone does not prove BridgeHost
  is alive or that telemetry is current. Exports are not a durable audit log.
- Assign one Collector pipeline/scrape target per BridgeHost instance unless a separately
  reviewed bounded instance-label scheme is introduced. These intentionally minimal resource
  labels do not distinguish multiple bridge processes merged into one Collector.

### Metric data policy

Instrument labels remain the bounded `transition` values (`connected`, `disconnected`,
`ready`, `not_ready`) and fixed readiness `reason` categories listed above. Gauges have
no instrument labels. OTLP resource metadata is restricted in code to the fixed
`service.name=GoldAiTrader.MT5.BridgeHost`; automatic instance IDs and environment resource
attributes are not included. No account, broker, symbol, command, request, or secret values
are exported. Do not enable arbitrary resource-to-label conversion in the Collector.
Scope labels are disabled by the example; Collector service/target metadata is fixed.
Prometheus adds its normal scrape job/instance labels for the configured target.

Initial state establishes a baseline and does not increment transition counters. Unchanged
observations do not increment counters or repeat health transition logs. Changes that occur
and recover entirely between observations may not be counted.

See the [OTLP SDK configuration reference](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)
and [Collector Prometheus exporter configuration](https://github.com/open-telemetry/opentelemetry-collector-contrib/tree/v0.162.0/exporter/prometheusexporter)
when reviewing deployment settings or dependency upgrades.
## HMAC authentication and replay

Every request, including an empty poll, requires exactly one `X-GAT-Bridge-Id`,
`X-GAT-Protocol-Version`, `X-GAT-Timestamp`, `X-GAT-Nonce`, and `X-GAT-Signature` header.
Bridge/protocol must match. Timestamp is UTC round-trip (`O`) within tolerance. Nonce is
16-128 ASCII letters, digits, hyphens, or underscores.

Canonical UTF-8 input is seven LF-separated lines with no trailing LF:

```text
bridge-id
protocol-version
UPPERCASE-HTTP-METHOD
/exact/request/path
exact-X-GAT-Timestamp-value
nonce
lowercase-sha256-of-exact-body-bytes
```

Send the 64-character hex HMAC-SHA256 as the signature. Hex case is accepted; decoded bytes
are compared in constant time. Any body encoding, whitespace, method, or path change fails.

Order is: declared-size check; bounded read; exact-body hash; header/timestamp/signature
validation; nonce reservation; strict JSON parse; protocol validation; ingestion. Only a valid
signature reserves a nonce. `(bridge id, nonce)` is accepted once in the timestamp window;
expired entries are purged and a full bounded cache fails closed. Oversize fails before auth,
replay insertion, or mutation.

## JSON, ingestion, and errors

`System.Text.Json` requires camel-case case-sensitive properties, string enums, mapped members,
no comments/trailing commas/numeric enums, and bounded depth. Protocol UTC rules still apply.

```json
{ "envelope": { "protocolVersion": 1, "bridgeInstanceId": "mt5-demo-01",
  "sentAtUtc": "2026-09-08T10:00:00.0000000+00:00" }, "terminalConnected": true }
```

Poll returns `204` when empty; otherwise JSON contains `commandType` and the concrete typed
`command`. The EA must echo exact bridge, protocol, command, correlation, and ownership identity.
`IMT5BridgeIngestionService` is the only route-to-adapter mutation boundary.

Errors contain only stable `code` and safe `message`: `400` malformed/invalid; `401` auth;
`409` replay/order/ack conflict; `413` too large; `415` wrong media type; `422` unsafe state;
`500` generic internal failure. Responses/logs omit secrets, signatures, nonces, bodies, stack
traces, paths, and raw exceptions. Security logs contain category, bridge, method, and path.

## Command delivery and safety

`MT5PollingExecutionTransport` has bounded in-memory pending storage and a synchronized,
bounded FIFO. Its command lifecycle is:

```text
Queued
  |
  v
Polled / Delivered
  |
  v
Acknowledged
```

Claim and terminal-state transitions are atomic. A queued command is either finalized before
claim or delivered; it cannot be reported as safely cancelled and then returned by a later
poll. Terminal behavior is deliberately delivery-aware:

| Wait outcome | Transport result | Safety meaning |
| --- | --- | --- |
| Cancelled before delivery | `OperationCanceledException` propagates | Safe cancellation; MT5 did not receive the command |
| Cancelled after delivery | `Indeterminate` | Broker outcome may be unknown; do not retry automatically |
| Timeout before delivery | `Rejected` | Known non-delivery; MT5 did not receive the command |
| Timeout after delivery | `Indeterminate` | Broker outcome is unknown; do not retry automatically |

Eligibility requires execution explicitly enabled, healthy host, recent authenticated session,
healthy connection/heartbeat, fresh current-session account, symbol specifications, and positions,
explicit Demo status, and existing universal execution/ownership checks. Unknown fails closed.

One poller claims a command. Ack must match a claimed command plus bridge/protocol identity.
Unknown, premature, mismatched, and duplicate acks cannot complete another command. Pending
capacity and queued IDs are released on every terminal path, and stale queued IDs are removed or
skipped so repeated pre-delivery cancellation/timeout cannot poison the FIFO.

Bounded in-memory tombstones distinguish acknowledged commands, known non-delivery, and delivered
commands that ended `Indeterminate`. A late acknowledgement for an indeterminate delivered
command is rejected with `acknowledgement_indeterminate`; it is not converted into success and
cannot complete a different command. NO_10 does not use late acknowledgements to update durable
execution state. Broker-position reconciliation remains authoritative for every `Indeterminate`
execution.

Pending delivery is in-memory. Restart makes interrupted delivery uncertain; the durable
execution journal remains the system of record.

## Deferred

Implemented: authenticated listener, strict bounded ingestion and replay defence, all adapter
routes, bounded command polling with matched acknowledgement in C#, and an MQL5 publisher for
heartbeat, connection, account, symbol, complete position inventory, and completed-bar telemetry.
The MQL5 publisher has no command polling or order API.

Automated verification on 2 October 2026 passed all 233 tests and the Release build with 0
warnings and 0 errors. It covers authentication/replay/malformed rejection, ordering,
reconnect/session invalidation, current-session account/symbol/position freshness, completed-bar
invalidation/idempotency/conflicts, stale-state failure, execution-disabled defaults, and static
telemetry-only terminal-source checks.

Actual Demo-terminal verification remains the NO_11 run on 30 September 2026. That revision
compiled with 0 errors and 0 warnings; authenticated connection, heartbeat, account, symbol,
position, and initial completed M5/M15 bar requests were accepted; and a runtime observation of
more than 35 minutes produced no further reported warnings. It does not validate the changed
NO_12 MQL5 source.

Pending manual verification is to compile the current NO_12 source and exercise controlled
disconnect/reconnect, heartbeat staleness, broker values, positions, and M5/M15 candles in Demo.

Deferred architecture remains terminal-side command delivery, acknowledgement, and execution;
secret rotation and process supervision; and every Live-trading review or enablement. Live
remains blocked.

### NO_13 validation status

Automated verification covers liveness independently of MT5 state; fail-closed readiness for
disconnected, missing, and stale heartbeat/account/symbol/position state; reconnect
invalidation/recovery; safe health responses; execution-disabled operation; and subscribable
health metrics.
Final NO_13 .NET validation passed all 244 tests. The full Release solution build completed with
0 warnings and 0 errors.

Manual NO_13 runtime validation was completed with the Demo terminal on 6 October 2026. Before
the EA was attached, liveness returned `200 healthy/process_alive` and readiness returned
`503 not_ready/terminal_disconnected`. After fresh heartbeat, account, XAUUSD symbol, and position
telemetry arrived, readiness returned `200 ready`. Removing the EA made readiness fail closed with
`503 terminal_disconnected` while liveness remained `200`; reattaching it restored fresh telemetry
and readiness returned `200` on the first captured post-reconnect sample.

The observed transition sequence was `connected`, `ready`, `disconnected`,
`not_ready (terminal_disconnected)`, `connected`, `ready`. Logs contained no secret, account or
broker identifier, authentication material, or raw telemetry. No intermediate post-reconnect
`503` was manually observed; missing and stale dependency states remain automated-test evidence.
No external metrics exporter or supervisor was validated. `System.Diagnostics.Metrics` remains an
automatically tested instrumentation contract. External execution remained false, MQL5 remained
unchanged and telemetry-only, and Live trading remains blocked.

See `platforms/mt5/GoldAiTraderBridge/README.md` for the manual checklist.
Transport correctness does not validate strategy performance or establish
production readiness.
