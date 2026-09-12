# MT5 authenticated loopback HTTP bridge

## Boundary

`GoldAiTrader.MT5.BridgeHost` is the HTTP/JSON boundary between a future chart-attached
MetaTrader 5 EA and `GoldAiTrader.Adapters.MT5`. It is a separate ASP.NET Core process,
keeping hosting, authentication, replay defence, parsing, and limits out of the adapter.

```text
Future GoldAiTraderBridge.mq5
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

This is transport, not strategy or risk authority. It includes no MQL5 EA, broker connector,
PostgreSQL/cTrader dependency, credential, or real order path.

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

All routes use `/bridge/mt5/v1`. POST requires `application/json`; queries are rejected.

| Method | Route | Contract |
| --- | --- | --- |
| POST | `/heartbeat` | `MT5HeartbeatMessage` |
| POST | `/connection` | `MT5ConnectionStateMessage` |
| POST | `/account` | `MT5AccountMessage` |
| POST | `/symbol` | `MT5SymbolMessage` |
| POST | `/positions` | `MT5PositionInventoryMessage` |
| POST | `/bars/completed` | `MT5CompletedBarMessage` |
| POST | `/execution/ack` | `MT5ExecutionAcknowledgement` |
| GET | `/commands/next` | `204` or one claimed command |

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
healthy connection/heartbeat, fresh current-session account and positions, explicit Demo status,
and existing universal execution/ownership checks. Unknown fails closed; Live remains blocked.

One poller claims a command. Ack must match a claimed command plus bridge/protocol identity.
Unknown, premature, mismatched, and duplicate acks cannot complete another command. Pending
capacity and queued IDs are released on every terminal path, and stale queued IDs are removed or
skipped so repeated pre-delivery cancellation/timeout cannot poison the FIFO.

Bounded in-memory tombstones distinguish acknowledged commands, known non-delivery, and delivered
commands that ended `Indeterminate`. A late acknowledgement for an indeterminate delivered
command is rejected with `acknowledgement_indeterminate`; it is not converted into success and
cannot complete a different command. The current implementation does not use late acknowledgements to update durable
execution state. Broker-position reconciliation remains authoritative for every `Indeterminate`
execution.

Pending delivery is in-memory. Restart makes interrupted delivery uncertain; the durable
execution journal remains the system of record.

## Deferred

Implemented: authenticated listener, strict bounded ingestion and replay defence, all adapter
routes, and bounded polling with matched acknowledgement. Deferred: real
`GoldAiTraderBridge.mq5`; MT5 serialization/extraction and broker mapping; durable delivery across
restart/reconnect; secret rotation and process supervision; real Demo integration/forward tests;
and every Live-trading review or enablement. Transport correctness does not validate strategy.
