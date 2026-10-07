# MetaTrader 5 bridge foundation

## Purpose

GoldAiTrader is the trading engine. MetaTrader 5 is a replaceable platform connector.

Strategy selection, risk sizing, ML decisions, durable execution identity, reconciliation,
operational readiness, and emergency policy remain in the C# engine. The chart-attached MQL5
expert advisor transports terminal observations only. Command polling and order execution are
deliberately absent from the terminal bridge foundation.

The intended boundary is:

```text
MT5 terminal/chart
        |
GoldAiTraderBridge.mq5
        |
loopback HTTP/JSON
        |
GoldAiTrader.Adapters.MT5
        |
Universal Platform Gateway
        |
GoldAiTrader Engine
```

NO_9 and NO_10 established the deterministic adapter and authenticated loopback host. The current
terminal-side milestone adds the telemetry-only `platforms/mt5/GoldAiTraderBridge` EA foundation.
It includes no embedded credential, command consumer, or order path.

NO_11 was validated on 30 September 2026: its MQL5 Expert Advisor compiled successfully in
MetaEditor with 0 errors and 0 warnings. That revision was tested using an MT5 Demo
account, with successful authenticated requests for connection, heartbeat, symbol,
account, positions, and completed M5/M15 bars. An extended runtime observation
of more than 35 minutes produced no further reported warnings.

The NO_11 .NET suite passed 230 tests and its complete Release build succeeded.

NO_12 automated validation on 2 October 2026 passed all 233 tests, including 81 focused MT5
bridge tests, and the complete Release build with 0 warnings and 0 errors. The tests cover
authenticated reconnect/session invalidation; fresh account, symbol, and position requirements;
completed-bar invalidation, exact-retry idempotency, conflict/ordering rejection; stale heartbeat
and snapshot failure; authentication, replay, malformed telemetry, loopback-only transport,
telemetry-only terminal source, and execution-disabled defaults.

The NO_12 MQL5 source adds a fail-closed backward-clock guard. NO_13 does not modify terminal
source. The validation evidence below distinguishes automated and Demo-terminal results; neither
establishes production readiness or authorizes live trading.

## Project and universal gateway

The adapter lives in `src/GoldAiTrader.Adapters.MT5` and references only:

- `GoldAiTrader.Core`
- `GoldAiTrader.Execution`
- `GoldAiTrader.Platform`

`MT5PlatformGateway` implements `ITradingPlatformGateway` and composes the existing provider
contracts. Its normalized platform name is `MetaTrader 5`; broker and bridge-instance identity
remain data, so universal code has no broker-specific or MT5-specific branches.

The default gateway advertises only capabilities implemented by trusted runtime bridge state:

- streaming completed bars;
- account information;
- explicit account-environment classification;
- canonical symbol mapping;
- symbol specifications;
- position inventory.

It intentionally does not advertise:

- historical bars;
- order submission, closure, or modification;
- stop-loss or take-profit execution;
- client-correlation round trips;
- ownership-metadata round trips;
- reconnect support.

A future execution transport adds structural execution capability flags only when its contract
explicitly preserves both correlation and ownership metadata. Transient `IsAvailable` and
`IsAuthenticated` values do not mutate or freeze the descriptor. They are rechecked whenever
execution-account readiness or an execution command is evaluated. `PlatformCapabilityValidator`
therefore rejects the default MT5 gateway for executable Demo startup, while a structurally
capable transport that is unavailable or unauthenticated still fails runtime Demo readiness and
sends no command.

## Protocol

`MT5BridgeProtocol.CurrentVersion` is the one supported protocol version. Every incoming
message carries an `MT5MessageEnvelope` containing:

- protocol version;
- bridge instance identifier;
- UTC sent timestamp.

The state rejects unsupported versions, missing identifiers, messages for another bridge, and
default or non-UTC timestamps.

Strongly typed protocol records cover:

- bridge identity;
- heartbeat and terminal connection state;
- account snapshots and explicit `AccountEnvironment`;
- symbol descriptions and specifications;
- position inventory;
- completed bars;
- submit, close, and modify commands;
- execution acknowledgements and normalized `ExecutionResult`.

Execution commands carry the identifiers needed by durable idempotency and reconciliation,
including `SignalId`, `ClientCorrelationId`, `StrategyVersion`, `OwnershipTag`, command
identity, account identity, and broker/canonical symbols where applicable.

Protocol and identity records never contain authentication secrets.

## Bridge identity

`MT5BridgeIdentity` distinguishes concurrent terminal instances with:

- `BridgeInstanceId`;
- `TerminalInstanceId`;
- `BrokerName`;
- `AccountIdentifier`;
- adapter version;
- protocol version.

All required identity fields must be non-empty. Broker names are not restricted to a known list.
The identity contains no password, token, account secret, or API credential.

## Runtime bridge state

`MT5BridgeState` owns the latest accepted in-memory snapshot. It stores:

- terminal connection and locally received heartbeat time;
- normalized account state;
- trusted symbol specifications;
- a replacement position inventory;
- latest completed bars and per-symbol/timeframe bar channels.

Mutable state is protected by a private lock. Completed-bar streams use thread-safe channels, and
symbol mappings are validated and copied on construction so callers cannot mutate them behind the
adapter.

Account, symbol, position, connection-state, and completed-bar messages use independent ordering
state appropriate to their snapshot or stream. Duplicate and older messages cannot overwrite a
newer accepted value. Completed bars are additionally ordered by canonical symbol and timeframe;
their intervals must be closed by both the envelope time and the trusted local UTC clock before
they are stored or emitted.

Account and position snapshots record local receipt times and have a configurable freshness
threshold. They must both be present and fresh in the current connected session before execution
readiness can succeed.

The state is intentionally not persisted to PostgreSQL. Durable trading state remains owned by
the existing execution journal, risk repository, and operational-safety repositories.

Providers return only trusted state. Account, symbol, position, latest-bar, and stream access fail
when bridge health is not current. Stale observations are not returned as healthy data.

## Heartbeat and connection health

`MT5BridgeOptions` defines heartbeat and snapshot freshness thresholds. Snapshot freshness
defaults to the heartbeat threshold, whose default is 15 seconds. `TimeProvider` supplies the
local receipt clock for deterministic evaluation.

Health requires:

1. the terminal connection flag to be true;
2. at least one received heartbeat;
3. heartbeat age to be non-negative and no greater than the configured threshold.

The heartbeat's sent timestamp must also be within that threshold and strictly newer than the
last accepted heartbeat. Delayed, duplicate, future-dated, and replayed heartbeats cannot refresh
connection readiness.

No timer or background thread is required. Health is recalculated whenever status or trusted data
is requested. A connection-state message cannot refresh an old heartbeat, so one historical good
message never leaves the adapter ready indefinitely.

Heartbeat and connection-state messages share connectivity ordering because both can change the
same terminal state. A disconnect invalidates account, position, specification, latest-bar, and
queued bar state and ends the current session. Reconnection alone does not restore execution
readiness: a fresh heartbeat plus new-session account and position snapshots are required, with a
fresh specification also required before normalized positions or orders can be processed.
`ReconnectSupport` remains unadvertised.

When stale, `MT5PlatformGateway` reports disconnected and `MT5ExecutionGateway` reports an
unhealthy execution account. Existing operational readiness and Demo startup checks therefore
remain fail-closed.


## Runtime health and observability foundation

The BridgeHost exposes safe loopback-only liveness and readiness probes. Liveness confirms only
that the process can serve requests. Readiness is a stricter operational signal: terminal
connected, fresh heartbeat, and fresh current-session account, configured symbol, and position
snapshots are all required. Any missing, stale, future-aged, or invalidated dependency returns a
bounded not-ready category and HTTP `503`. Readiness never grants execution authority.

Connection and readiness logs are emitted only on state changes. The built-in
`System.Diagnostics.Metrics` meter `GoldAiTrader.MT5.BridgeHost` exposes connected/readiness
gauges, heartbeat age, bounded transitions, and telemetry freshness failures. NO_13 implements no
exporter or production supervisor; this instrumentation contract may be consumed by a future
approved exporter, supervisor, or observability integration.

External execution remains disabled. These probes observe the existing fail-closed state; they do
not bypass strategy, risk, gateway, account-environment, ownership, or execution controls.

## Account environment

The bridge must explicitly supply one of the existing values:

- `AccountEnvironment.Unknown`
- `AccountEnvironment.Demo`
- `AccountEnvironment.Live`

The adapter does not infer Demo status from a server name, broker name, account number, or terminal
label. Unknown remains unknown. Live remains non-executable. Only a fresh, connected, explicitly
Demo account can satisfy the existing execution-account check, and Live platform suitability is
still rejected by universal policy.

## Symbol normalization

GoldAiTrader continues to use canonical symbols such as `XAUUSD`. Broker symbols are configured
per adapter instance rather than hard-coded globally.

`MT5SymbolMapper` validates the configured canonical-to-broker mapping and produces the existing
`SymbolSpecification`, including tick size, tick value per volume unit, contract size, volume
limits and step, minimum stop distance, broker symbol, canonical symbol, and trading availability.

Missing mappings, mismatched symbols, non-positive required values, invalid volume boundaries, or
unavailable trading fail closed. Position and completed-bar messages must match a previously
trusted symbol specification.

## Provider implementations

The bridge state backs these existing contracts:

- `MT5TradingAccountProvider : ITradingAccountProvider`
- `MT5SymbolSpecificationProvider : ISymbolSpecificationProvider`
- `MT5PositionProvider : IPositionProvider`
- `MT5MarketDataProvider : IMarketDataProvider`

Market data consists only of closed, per-stream ordered completed bars received after bridge
validation. Exact retries of the latest completed bar are idempotent; conflicting same-time,
older, future, and still-open bars are rejected before storage or channel emission. Historical
retrieval is not implemented and `IHistoricalMarketDataProvider` is null.

Position DTOs preserve GoldAiTrader identity metadata without manufacturing it. A position lacking
`SignalId`, client correlation, strategy version, and ownership tag remains manual/unowned.
Partial metadata remains ambiguous under the existing `PositionOwnership` rules.

## Execution boundary

`MT5ExecutionGateway` implements the existing `IExecutionGateway`; it does not bypass
`GatewayTradeExecutor`.

`IMT5LoopbackExecutionTransport` defines the command/acknowledgement boundary. NO_10 adds the
bounded in-memory polling implementation in the separate `GoldAiTrader.MT5.BridgeHost` process.
See `docs/mt5-loopback-http-bridge.md` for its authenticated wire contract and safety limits.

Even a future transport is ineligible unless it is:

- available;
- authenticated;
- configured through a validated `http://127.0.0.1` endpoint;
- able to preserve client correlation identifiers;
- able to preserve GoldAiTrader ownership metadata.

Execution rechecks fresh bridge health, current-session account, symbol, and position snapshots,
explicit Demo environment, transport availability, transport authentication, and owned-position
metadata.
The submit correlation remains `GoldAiTrader-{SignalId:N}`, matching the durable execution
journal. Unexpected acknowledgement identity produces an indeterminate result rather than an
unsafe retry assumption.

## Loopback and authentication

The NO_10 endpoint binds only to `127.0.0.1`. It must never bind to `0.0.0.0`, a LAN
interface, or an Internet-facing address. `MT5LoopbackEndpoint` rejects non-loopback hosts,
non-HTTP schemes, and credentials embedded in a URI.

HTTP/JSON requests authenticate with HMAC-SHA256 over bridge and protocol identity, method,
exact path, UTC timestamp, nonce, and the exact request-body hash. The secret is supplied outside
source control, signatures are compared in constant time, replay storage is bounded, and secrets,
signatures, and payloads are never logged. Authentication failure returns no trusted state and
permits no command execution.

## Terminal message flow

The thin `GoldAiTraderBridge.mq5` EA sends:

1. heartbeat and connection state;
2. account snapshot with explicit environment;
3. symbol specifications;
4. full position inventory;
5. completed bars.

The current EA never polls for commands, submits orders, or acknowledges executions. Those paths
remain intentionally deferred even though the C# host already defines a bounded command protocol.

The EA must not calculate strategy signals, choose risk, recover losses, own persistence, resolve
reconciliation discrepancies, or decide whether Live trading is allowed.

## NO_12 validation evidence

Automated verification covers strict authentication, replay and malformed-message rejection,
message ordering, stale heartbeat/snapshot failure, reconnect invalidation, fresh post-reconnect
state, completed-bar invalidation/idempotency/conflict handling, and disabled execution. A static
terminal-source test rejects order APIs, command routes, non-loopback defaults, and direct secret
logging patterns.

Actual Demo-terminal verification remains the NO_11 run on 30 September 2026: successful compile,
authenticated telemetry, initial M5/M15 bars, and more than 35 minutes without further reported
warnings.

NO_12 manual Demo validation was completed on 5 October 2026. The current MQL5
Expert Advisor compiled in MetaEditor with 0 errors and 0 warnings. Controlled
terminal stop/reconnect testing confirmed telemetry cessation and fresh
repopulation of heartbeat, symbol, account, position, and completed-bar state.

The MT5 account snapshot, XAUUSD symbol specification, and empty position
inventory were compared directly with the Demo terminal. Live completed M5 and
M15 candles matched MT5 OHLC and tick-volume values, and BridgeHost accepted
them only after their intervals had closed.

The observed broker-server-to-UTC offset was consistent for the validated
samples. A real DST-transition runtime test was not exercised and remains
outside the evidence claimed here.

Final NO_12 verification passed all 233 .NET tests with zero failures, and the
complete Release solution build succeeded. These results validate the tested
telemetry and fail-closed integration behavior; they do not establish strategy
profitability, production readiness, or authorize live trading.

## NO_13 automated and runtime status

The focused NO_13 automated suite verifies independent liveness; readiness only with fresh
current-session heartbeat/account/symbol/position state; missing and stale categories;
disconnect/reconnect invalidation and recovery; identifier-safe responses; execution remaining
disabled; and subscribable built-in metrics.
Final NO_13 .NET validation passed all 244 tests. The complete Release solution build succeeded
with 0 warnings and 0 errors.

Manual NO_13 runtime validation was completed with the Demo terminal on 6 October 2026. Before
attachment, `/health/live` returned `200 healthy/process_alive` and `/health/ready` returned
`503 not_ready/terminal_disconnected`. Fresh heartbeat, account, XAUUSD symbol, and position
telemetry made readiness return `200 ready`. Removing the EA left liveness at `200` and made
readiness fail closed with `503 terminal_disconnected`; reattachment restored fresh telemetry and
the first captured post-reconnect readiness sample was already `200 ready`.

Transition logs showed `connected`, `ready`, `disconnected`,
`not_ready (terminal_disconnected)`, `connected`, `ready` without secrets, account or broker
identifiers, authentication material, or raw telemetry. No intermediate post-reconnect `503` is
claimed; automated tests remain the evidence for intermediate missing/stale dependency states.
No external exporter or supervisor was manually validated. `System.Diagnostics.Metrics` remains
an instrumentation contract validated by automated tests. External execution remained false,
MQL5 remained unchanged and telemetry-only, and Live trading remains blocked.

## Deferred work

The remaining terminal integration work is:

- validate broker-specific symbol, tick-value, stop-level, volume, and time-zone observations;
- run authenticated disconnect/reconnect and completed-bar forward tests;
- design and review durable command delivery, acknowledgement, and broker reconciliation before
  adding any terminal-side command or order API;
- integrate the health contract with a reviewed local supervisor and secret-rotation procedure.

See `platforms/mt5/GoldAiTraderBridge/README.md` for installation and manual validation. Live
execution remains disabled and requires a separate future safety review.
