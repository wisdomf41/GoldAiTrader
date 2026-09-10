# GoldAiTrader

GoldAiTrader is a modular C#/.NET algorithmic trading research and execution platform focused initially on XAUUSD (Gold).

The project is designed around broker independence, deterministic strategy development, controlled risk, realistic backtesting, secure execution boundaries, and demo-first validation.

## Project Goals

GoldAiTrader is being developed to support:

- systematic trading strategy research
- risk-based position sizing
- drawdown and loss-limit protection
- deterministic technical-indicator pipelines
- realistic historical backtesting
- out-of-sample and walk-forward validation
- machine-learning assisted trade filtering
- broker/platform-independent execution
- durable execution journaling and reconciliation
- secure MetaTrader 5 bridge integration
- demo-first forward testing

The project does not assume or guarantee profitability.

## Technology Stack

- C#
- .NET 10
- ASP.NET Core
- xUnit
- PostgreSQL
- Entity Framework Core
- ML.NET
- MetaTrader 5 integration foundation
- cTrader adapter foundation
- Git / GitHub

## Architecture

GoldAiTrader separates trading logic from broker and platform integrations.

Core components include:

- `GoldAiTrader.Core` — shared domain models and primitives
- `GoldAiTrader.Market` — market indicators and market-data logic
- `GoldAiTrader.Strategy` — deterministic trading strategies
- `GoldAiTrader.Risk` — position sizing and portfolio risk controls
- `GoldAiTrader.Execution` — execution safety, journaling, ownership, and reconciliation
- `GoldAiTrader.Backtesting` — historical strategy simulation
- `GoldAiTrader.Data` — PostgreSQL and Entity Framework Core persistence
- `GoldAiTrader.ML` — machine-learning research and filtering components
- `GoldAiTrader.Bot` — runtime coordination and startup safety
- `GoldAiTrader.Platform` — universal trading-platform gateway
- `GoldAiTrader.Adapters.MT5` — MetaTrader 5 protocol and gateway foundation
- `GoldAiTrader.Adapters.CTrader` — cTrader integration boundary
- `GoldAiTrader.MT5.BridgeHost` — authenticated local HTTP/JSON bridge host
- `GoldAiTrader.Core.Tests` — automated test suite

## Trading Strategy Foundation

The initial strategy research focuses on XAUUSD using:

- M15 market context
- M5 trade execution
- EMA trend structure
- ADX trend-strength filtering
- RSI momentum filtering
- ATR-based volatility and stop calculations
- pullback-based entries
- configurable risk/reward targets
- spread and session filters
- trade cooldown and frequency controls

Strategy rules remain deterministic so they can be reproduced consistently in testing and backtesting.

## Risk Management

Risk controls are treated as a first-class part of the architecture.

Current safeguards include:

- risk-based position sizing
- mandatory stop-loss protection
- configurable per-trade risk
- daily and weekly loss limits
- drawdown warning and hard-stop thresholds
- consecutive-loss suspension
- position-count limits
- spread and volatility protection
- execution-readiness checks
- Demo-account verification
- broker-position ownership checks
- durable execution journaling
- reconciliation of indeterminate broker outcomes

## Platform Independence

The trading engine is designed so strategy, risk, market, and execution logic remain independent of a specific broker or trading platform.

Platform-specific behavior is isolated behind gateway and adapter boundaries.

Current integration work includes:

- MetaTrader 5 gateway and bridge protocol
- authenticated loopback HTTP/JSON bridge host
- cTrader adapter boundary
- extensibility for additional REST, FIX, or broker-specific adapters

## MetaTrader 5 Bridge Security

The MT5 bridge host currently provides a secure local communication foundation with:

- explicit `127.0.0.1` loopback binding
- HMAC-SHA256 request authentication
- timestamp validation
- nonce/replay protection
- bounded request payloads
- strict JSON parsing
- authenticated command polling
- execution acknowledgement handling
- bounded command lifecycle tracking
- fail-closed cancellation and timeout semantics
- reconciliation requirements for indeterminate outcomes

The actual MQL5 terminal-side bridge is not yet part of the repository.

## Persistence

PostgreSQL and Entity Framework Core are used for operational persistence.

The persistence layer includes support for:

- execution journal records
- broker correlation identifiers
- execution lifecycle state
- risk state
- emergency state
- broker-position reconciliation

Database migrations are committed explicitly and are not applied automatically during normal startup.

## Machine Learning

ML.NET is reserved for research-stage trade filtering.

Machine learning is not intended to replace deterministic strategy or risk logic.

Any ML component must be validated against leakage, overfitting, and out-of-sample performance before it can influence execution decisions.

## Build

Restore dependencies:

`dotnet restore`

Build the solution:

`dotnet build`

Run the full test suite:

`dotnet test`

## Current Safety Status

GoldAiTrader remains development and research software.

Live trading is disabled by policy.

External execution must remain:

- explicitly enabled
- Demo-only
- account-verified
- platform-ready
- risk-approved
- reconciliation-aware

No component should automatically enable Live trading.

## Documentation

Detailed design documentation is available in the `docs` directory, including:

- architecture
- strategy
- risk policy
- backtesting
- machine learning
- execution safety
- operational persistence
- platform independence
- MT5 bridge architecture
- deployment and runtime guidance

## Contributing

Contributions are welcome.

Please read `CONTRIBUTING.md` before submitting changes.

Changes affecting execution, risk, persistence, reconciliation, or platform integrations should include appropriate automated tests and preserve the project's fail-closed safety model.

## Development Status

The project currently includes the core trading architecture, risk engine, deterministic strategy foundation, backtesting infrastructure, persistence model, universal platform gateway, MT5 adapter foundation, authenticated MT5 loopback bridge host, and automated safety testing.

Current development remains focused on completing platform integration, realistic market-data validation, demo forward testing, and further operational hardening before any consideration of live trading.
