# MetaTrader 5 bridge foundation

## Purpose

GoldAiTrader is the trading engine. MetaTrader 5 is a replaceable platform connector.

Strategy selection, risk sizing, ML decisions, durable execution identity, reconciliation,
operational readiness, and emergency policy remain in the C# engine. A future chart-attached
MQL5 expert advisor only transports terminal observations and already-approved commands.

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

The current implementation provides only the deterministic C# foundation. It does not include an EA, HTTP server,
broker connection, credential, or real order path.

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
validation. Duplicate, older, future, and still-open bars are rejected before storage or channel
emission. Historical retrieval is not implemented and `IHistoricalMarketDataProvider` is null.

Position DTOs preserve GoldAiTrader identity metadata without manufacturing it. A position lacking
`SignalId`, client correlation, strategy version, and ownership tag remains manual/unowned.
Partial metadata remains ambiguous under the existing `PositionOwnership` rules.

## Execution boundary

`MT5ExecutionGateway` implements the existing `IExecutionGateway`; it does not bypass
`GatewayTradeExecutor`.

`IMT5LoopbackExecutionTransport` defines the command/acknowledgement boundary. The current implementation adds the
bounded in-memory polling implementation in the separate `GoldAiTrader.MT5.BridgeHost` process.
See `docs/mt5-loopback-http-bridge.md` for its authenticated wire contract and safety limits.

Even a future transport is ineligible unless it is:

- available;
- authenticated;
- configured through a validated `http://127.0.0.1` endpoint;
- able to preserve client correlation identifiers;
- able to preserve GoldAiTrader ownership metadata.

Execution rechecks fresh bridge health, current-session account and position snapshots, explicit
Demo environment, transport availability, transport authentication, and owned-position metadata.
The submit correlation remains `GoldAiTrader-{SignalId:N}`, matching the durable execution
journal. Unexpected acknowledgement identity produces an indeterminate result rather than an
unsafe retry assumption.

## Loopback and authentication

The loopback bridge endpoint binds only to `127.0.0.1`. It must never bind to `0.0.0.0`, a LAN
interface, or an Internet-facing address. `MT5LoopbackEndpoint` rejects non-loopback hosts,
non-HTTP schemes, and credentials embedded in a URI.

HTTP/JSON requests authenticate with HMAC-SHA256 over bridge and protocol identity, method,
exact path, UTC timestamp, nonce, and the exact request-body hash. The secret is supplied outside
source control, signatures are compared in constant time, replay storage is bounded, and secrets,
signatures, and payloads are never logged. Authentication failure returns no trusted state and
permits no command execution.

## Future message flow

A later thin `GoldAiTraderBridge.mq5` EA will send:

1. heartbeat and connection state;
2. account snapshot with explicit environment;
3. symbol specifications;
4. full position inventory;
5. completed bars.

After strategy and risk approval in C#, GoldAiTrader may send an idempotent execution command.
The EA will submit that already-approved command and return an acknowledgement containing broker
order/position identity and the normalized outcome.

The EA must not calculate strategy signals, choose risk, recover losses, own persistence, resolve
reconciliation discrepancies, or decide whether Live trading is allowed.

## Deferred work

Before creating the real MQL5 EA, the next phase must still implement and test:

- MT5-side protocol serialization and version negotiation;
- terminal extraction of account, symbols, positions, and completed bars;
- exact MT5 volume, tick-value, stop-level, filling-mode, and error-code mapping;
- durable command acknowledgement and reconnect/reconciliation behavior;
- real Demo-only integration and forward testing.

Only after that C# boundary is proven should `GoldAiTraderBridge.mq5` be created. Live execution
remains disabled and requires a separate future safety review.
