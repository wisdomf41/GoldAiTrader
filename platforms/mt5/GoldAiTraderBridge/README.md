# GoldAiTrader MetaTrader 5 telemetry bridge

This directory contains the terminal-side foundation for the existing authenticated
GoldAiTrader MT5 loopback bridge. It publishes terminal state only. It does not poll for
commands, acknowledge commands, submit orders, modify positions, or close positions.

## Safety boundary

- The only permitted endpoint is an exact `http://127.0.0.1:<port>` base URL.
- The EA refuses to start with a missing/short external secret or invalid configuration.
- The secret is read from the MetaTrader common-files sandbox and is never logged.
- Account type comes from `ACCOUNT_TRADE_MODE`; Demo is never inferred from names.
- Unknown/contest and Live telemetry remain non-authorizing. C# execution stays disabled.
- A position inventory is withheld if any open position uses an unmapped symbol or lacks the
  entry, volume, or stop-loss data required to calculate real monetary risk.
- Ownership fields are always `null`; this phase cannot prove that arbitrary terminal
  positions belong to GoldAiTrader.
- Only completed bars (`CopyRates` position 1) are published.

## Files

- `GoldAiTraderBridge.mq5`: EA lifecycle, reconnect handling, bounded backoff, scheduling.
- `Include/GoldAiTraderBridgeConfig.mqh`: routes, strict loopback validation, UTC clock,
  external secret-file loading.
- `Include/GoldAiTraderBridgeCrypto.mqh`: UTF-8 SHA-256 and RFC 2104 HMAC-SHA256.
- `Include/GoldAiTraderBridgeHttp.mqh`: exact-body signing and authenticated POST requests.
- `Include/GoldAiTraderBridgeJson.mqh`: deterministic protocol JSON primitives.
- `Include/GoldAiTraderBridgeState.mqh`: account, symbol, position, and completed-bar collection.
- `Config/bridge-host.settings.example.json`: placeholder-only C# host configuration shape.
- `Config/bridge.secret.example`: placeholder-only secret-file template.

## Installation

1. In MetaTrader 5, open `File -> Open Data Folder`.
2. Copy the `GoldAiTraderBridge` directory into `MQL5/Experts/Advisors/` while preserving `Include/`.
3. Open `GoldAiTraderBridge.mq5` in MetaEditor and compile it locally.
4. In `Tools -> Options -> Expert Advisors`, add the exact host URL, for example the configured
   `http://127.0.0.1:5088`, to the allowed WebRequest URLs.
5. Generate a private random secret of at least 32 UTF-8 bytes outside the repository. Create
   `Terminal/Common/Files/GoldAiTrader/bridge.secret` containing only that value. Do not copy the
   `.example` placeholder as a usable credential.
6. Configure the C# host with the same secret through
   `GOLDAITRADER_MT5_BRIDGE_SECRET` (preferred) or an external configuration provider.
7. Configure identical bridge ID, numeric MT5 login/account identifier, protocol `1.0`, and the
   explicit `XAUUSD` to broker-symbol mapping on both sides.
8. Start `GoldAiTrader.MT5.BridgeHost`, attach the EA to a chart in a Demo terminal, set
   `InpBrokerSymbol` to the exact broker symbol, then enable the terminal automation runtime.

`WebRequest` is synchronous and unavailable in the MetaTrader Strategy Tester.
This bridge must be validated in a locally installed Demo terminal.

NO_11 was validated on 30 September 2026: its MQL5 Expert Advisor compiled successfully in
MetaEditor with 0 errors and 0 warnings and was attached to an MT5 Demo terminal.

Authenticated connection, heartbeat, account, symbol, positions, and initial
completed M5/M15 bar requests were accepted by the C# BridgeHost.
An extended runtime observation of more than 35 minutes produced no further
reported warnings.

The NO_11 .NET suite passed 230 tests and its complete Release build succeeded.

NO_12 automated validation on 2 October 2026 passed all 233 .NET tests, including 81 focused
MT5 bridge tests, and the complete Release build succeeded with 0 warnings and 0 errors.
Automated coverage verifies authenticated reconnect/session invalidation; fresh account, symbol,
and position requirements; completed-bar invalidation, idempotency, conflict/ordering checks;
stale heartbeat and snapshot failure; authentication, replay, and malformed-payload rejection;
loopback-only transport; telemetry-only MQL5 source; and execution-disabled defaults.

NO_12 added a fail-closed guard for backward UTC clock movement and made symbol
specifications freshness-bound execution prerequisites. The repository records NO_12 automated
and Demo-terminal evidence below. NO_13 does not change MQL5 source. Neither automated nor Demo
telemetry validation establishes production readiness or authorizes trading execution.


## BridgeHost health and observability

The C# host now exposes `GET /health/live` and `GET /health/ready` on the same exact loopback
listener. Liveness reports only that the process is serving. Readiness returns `200` only after
fresh current-session connection, heartbeat, account, configured symbol, and position telemetry;
otherwise it returns `503` with a fixed safe category.

Health responses contain no secret, account, terminal, bridge, or broker identifiers. Transition
logs are emitted only when connection or readiness changes. The built-in
`GoldAiTrader.MT5.BridgeHost` metrics meter is an instrumentation contract for a future approved
exporter, supervisor, or observability integration. NO_13 does not implement an exporter or
production supervisor.
Readiness is observational and works with
`MT5Bridge__ExternalExecutionEnabled=false`; it never authorizes orders.

## Published state

The EA sends the existing protocol `1.0` routes for heartbeat, connection, account, symbol,
positions, and completed bars. Every request uses these exact headers:

- `X-GAT-Bridge-Id`
- `X-GAT-Protocol-Version`
- `X-GAT-Timestamp`
- `X-GAT-Nonce`
- `X-GAT-Signature`

The HMAC input is the exact seven-line format documented in
`docs/mt5-loopback-http-bridge.md`. Body bytes are UTF-8 and are hashed before transmission.

The symbol snapshot reads tick size, losing-position tick value, contract size, min/max/step
volume, stop level, and trade availability directly from the configured broker symbol. Completed
M5 and M15 bars are enabled by default and other `ENUM_TIMEFRAMES` can be added without changing
the transport protocol.

Daily, weekly, and peak risk references are terminal-observed values persisted in MT5 terminal
global variables per account. On the first attachment they begin at the currently observed
balance/equity; execution remains disabled, so a future Demo execution phase must validate these
references across day/week boundaries and restarts before relying on them for order authority.

## Manual Demo validation checklist

### 1. Installation and configuration

- [x] Compile the current NO_12 EA with zero errors and zero warnings (5 October 2026).
- [x] Install and attach the EA from `MQL5/Experts/Advisors/GoldAiTraderBridge`.
- [x] Configure the external HMAC secret in the MT5 common-files directory and C# BridgeHost.
- [x] Confirm BridgeHost uses `127.0.0.1:5088`.
- [x] Confirm `MT5Bridge__ExternalExecutionEnabled` remains set to `false`.
- [ ] Independently verify that the MT5 WebRequest allowlist contains only the intended loopback URL.

### 2. Demo account and authentication

- [x] Confirm the terminal is connected to the intended MT5 Demo account.
- [x] Confirm authenticated connection requests are accepted by BridgeHost.
- [x] Confirm authenticated heartbeat requests return HTTP 204.
- [x] Complete an extended runtime observation of more than 35 minutes without further reported warnings during NO_11 validation.
- [x] Compare Demo account balance, equity, free margin, open-position count, and Demo classification directly between MT5 and BridgeHost.
- [ ] Independently compare the account currency through BridgeHost runtime state.
- [ ] Independently compare the account identifier without exposing it in logs, screenshots, or repository content.

### 3. XAUUSD symbol telemetry

- [x] Confirm BridgeHost accepts authenticated symbol telemetry requests with HTTP 204.
- [x] Verify canonical `XAUUSD` maps to the exact broker symbol `XAUUSD`.
- [x] Compare contract size, minimum volume, maximum volume, volume step, stop level, and trading availability against MT5.
- [x] Confirm BridgeHost receives `TickSize = 0.01` and `TickValuePerVolumeUnit = 1`.
- [ ] Independently verify tick size and tick value against a separate MT5-side source or broker specification.

### 4. Position synchronization

- [x] Confirm BridgeHost accepts position inventory requests with HTTP 204.
- [x] Verify the received position inventory matches the actual MT5 terminal state with zero open positions.
- [x] Confirm the account snapshot also reports `OpenPositions = 0`.
- [ ] Verify manual and unowned positions have null ownership metadata using an actual Demo position.
- [ ] Verify incomplete or unmapped position data is rejected or withheld during a live terminal integration scenario as required by the risk policy.

### 5. Completed candle telemetry

- [x] Confirm authenticated completed-bar requests are accepted by BridgeHost with HTTP 204.
- [x] Confirm the runtime produces no completed-bar validation warnings after the timestamp fixes.
- [x] Independently verify a live M5 completed candle against MT5 timestamp, OHLC, and tick volume.
- [x] Independently verify a live M15 completed candle against MT5 timestamp, OHLC, and tick volume.
- [x] Confirm the validated M5 candle was accepted only after its five-minute interval had closed.
- [x] Confirm the validated M15 candle was accepted only after its fifteen-minute interval had closed.
- [x] Confirm the tested forming-candle cases were not accepted as completed bars.
- [x] Confirm broker-server-time to UTC conversion was consistent for the validated M5 and M15 samples.
- [ ] Verify completed-bar delivery across several consecutive M5 and M15 candle boundaries.
- [ ] Perform a real broker UTC/DST transition runtime validation.

### 6. Connection recovery and fail-closed behavior

- [x] Remove the EA and confirm heartbeat, symbol, account, position, and completed-bar telemetry stops.
- [x] Reattach the EA and confirm fresh connection, heartbeat, symbol, account, position, and completed-bar telemetry is republished.
- [x] Confirm fresh account, symbol, and position state is required before execution readiness can become valid.
- [x] Confirm automated tests invalidate trusted symbol state and completed bars across reconnect.
- [x] Confirm automated tests reject stale account, symbol, position, and heartbeat state for execution readiness.
- [x] Confirm stale or disconnected state fails closed and cannot authorize execution in the automated safety suite.
- [x] Confirm automated tests cover authentication failure, malformed telemetry, and replay rejection.
- [ ] Perform a separate controlled terminal/network disconnect and reconnect test, distinct from EA removal and reattachment.
- [ ] Directly observe BridgeHost runtime stale-state transition after heartbeat loss through a dedicated safe status mechanism.
- [ ] Perform authentication-failure and replay-rejection tests against the actual Demo terminal integration.

### 7. Trading execution safety

- [x] Confirm `MT5Bridge__ExternalExecutionEnabled` is set to `false`.
- [x] Confirm the current MQL5 bridge remains telemetry-only.
- [x] Confirm automated source verification rejects MQL5 order APIs.
- [x] Confirm automated source verification rejects command polling.
- [x] Confirm automated source verification rejects execution acknowledgement routes in the MQL5 source.
- [x] Confirm automated source verification enforces the loopback-only transport design.
- [x] Confirm no real bridge secret or MT5 account identifier appears in the NO_12 Git diff.
- [x] Confirm the real `bridge.secret` file remains excluded by `.gitignore`.

### 8. Automated testing and builds

- [x] Pass all 233 .NET automated tests with zero failures and zero skipped tests in the final NO_12 run (5 October 2026).
- [x] Pass the focused NO_12 MT5 bridge automated test suite.
- [x] Build the complete .NET solution successfully in Release configuration.
- [x] Compile the NO_11 MQL5 Expert Advisor with zero errors and zero warnings.
- [x] Compile the current NO_12 MQL5 Expert Advisor after its timestamp-ordering guard change with zero errors and zero warnings.
- [x] Pass `git diff --check`.
- [x] Confirm the NO_12 changed-file set contains only the expected eight files.
- [x] Confirm the real bridge secret is absent from the Git diff.
- [x] Confirm the real MT5 account identifier is absent from the Git diff.
- [x] Confirm `platforms/mt5/GoldAiTraderBridge/Config/bridge.secret` remains ignored by Git.
- [x] Pass all 244 .NET automated tests in the final NO_13 run.
- [x] Pass all 11 focused NO_13 health tests.
- [x] Build the complete NO_13 solution in Release with 0 warnings and 0 errors.


### 9. NO_13 health runtime validation — completed 6 October 2026

The following checks were completed with the unchanged telemetry-only EA on the intended Demo
terminal. No credentials or account identifiers were captured or published.

- [x] Before attaching MT5, verify `GET /health/live` returns `200 healthy/process_alive` and
  `GET /health/ready` returns `503 not_ready/terminal_disconnected`.
- [x] Attach the unchanged Demo EA; after fresh heartbeat, account, XAUUSD symbol, and positions
  arrive, verify readiness returns `200 ready`.
- [x] Remove the EA; verify readiness returns `503 terminal_disconnected` while liveness remains
  `200`.
- [x] Reattach the EA; verify fresh telemetry and connection recover and readiness returns `200`.
  The first captured post-reconnect poll was already ready, so no intermediate runtime `503` is
  claimed; automated tests verify intermediate missing and stale dependency states.
- [x] Observe the transition sequence `connected`, `ready`, `disconnected`,
  `not_ready (terminal_disconnected)`, `connected`, `ready`.
- [x] Confirm transition logs contain no secret, account or broker identifier, authentication
  material, or raw telemetry payload.
- [x] Confirm external execution remains false, MQL5 remains telemetry-only, and Live remains
  blocked.
No external metrics exporter or supervisor was manually validated. Metrics remain an
instrumentation contract validated by automated tests; optional .NET diagnostics or in-process
observation may be performed later.

### 10. Operational readiness — pending

- [ ] Validate broker-specific behavior across additional market conditions.
- [ ] Validate multiple consecutive M5 and M15 candle boundaries.
- [ ] Perform a real UTC/DST transition validation.
- [ ] Perform extended terminal/network disconnect and reconnect testing.
- [ ] Implement and test production supervision, restart, readiness-routing, and recovery behavior.
- [ ] Establish and test secure HMAC secret rotation.
- [ ] Validate ownership metadata using actual Demo positions before any execution work.
- [ ] Complete additional failure-recovery and adverse-network testing.
- [ ] Complete all required security and risk reviews before considering execution capabilities.
- [ ] Keep Live trading disabled until a separate explicit Live-readiness review is completed.

## Deferred

Demo order execution, command polling, acknowledgement delivery, durable terminal-side command
state, ownership metadata round-tripping, and Live execution are deliberately not implemented.
Live trading is neither approved nor configurable through this EA.
