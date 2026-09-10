# GoldAiTrader — Final Technical Report

## Status summary

| Status | Result |
| --- | --- |
| SOFTWARE COMPLETE | BASELINE ENGINEERING IMPLEMENTED; platform integration items remain |
| STRATEGY VALIDATED | PENDING — no trustworthy XAUUSD historical dataset was available |
| ML VALIDATED | PENDING — no leakage-safe labeled real dataset was available |
| DEMO READY | PENDING HUMAN cTrader DEMO INTEGRATION TESTING |
| LIVE APPROVED | NO — live execution is intentionally blocked |

These statuses are independent. Passing software tests is not evidence of profitability.

## Architecture and projects

The solution is a modular .NET 10 implementation:

- Core: broker-independent domain values and decisions.
- Market: chronological validation plus EMA, ATR, RSI, ADX and session classification.
- Risk: equity-based 0.50% default risk sizing, volume normalization, exposure, spread, daily/weekly loss, balance/equity drawdown and emergency shutdown controls.
- Strategy: deterministic TrendPullbackV1 producing BUY, SELL or WAIT candidates. Risk retains final authority.
- Execution: simulated executor and a bounded, duplicate-safe cTrader demo gateway boundary. Live configuration fails closed.
- Backtesting: chronological completed-frame processing, shared strategy/risk logic, costs, conservative same-bar ambiguity, curves, trade/decision records, metrics, chronological splitting and stress scenarios.
- Data: EF Core/PostgreSQL context and entities for trades, features, decisions, predictions, research runs and model metadata.
- ML: ML.NET logistic baseline, schema/model versioning, disabled/shadow/filter modes, training-cutoff leakage checks and chronological walk-forward folds.
- Bot: startup validation and operational guards for stale data, connection loss, shutdown and emergency state.

## Implemented safety controls

The implementation prohibits live execution, defaults to Shadow, requires valid stop placement, normalizes volume downward, refuses broker minimum volume when it would exceed risk, limits initial open positions to one, applies aggregate exposure limits, rejects excessive spread, and fails closed for invalid data, stale market data, disconnects and shutdown.

Martingale, grid trading, doubling after losses and uncontrolled averaging down are absent.

## Build and tests

The final quality gate uses:

```
dotnet restore GoldAiTrader.slnx
dotnet build GoldAiTrader.slnx --no-restore
dotnet test GoldAiTrader.slnx --no-build
```

Automated tests cover domain validation, chronology, indicators, risk sizing and hard boundaries, strategy confirmation/cooldown/session behavior, duplicate execution, conservative same-bar handling, chronological splits, ML leakage, walk-forward ordering, non-live defaults, stale data and emergency shutdown.

## Backtesting and research status

The engine is deterministic for identical ordered inputs and uses only supplied completed frames. Synthetic fixtures test chronology and execution behavior. No historical XAUUSD performance is reported or fabricated.

A real dataset must contain timezone-explicit XAUUSD bid/ask M5 bars, sufficient M15 history, broker-consistent tick value/volume rules, spread, commissions, and preferably observed slippage across multiple regimes. Development, validation and locked OOS periods must remain chronological.

STRATEGY VALIDATION: PENDING.

## ML status

ML is initially a quality filter only and cannot override risk. Disabled mode leaves the deterministic baseline unchanged; shadow mode records predictions without filtering. Training rejects observations after the declared cutoff, and walk-forward folds train only on earlier rows.

No real labeled dataset was available, so baseline-versus-filter performance was not evaluated.

ML VALIDATION: PENDING. Keep ML disabled.

## Persistence status

The PostgreSQL EF Core model is implemented without credentials. Tests do not require a production database. A reviewed initial migration and PostgreSQL integration test remain deployment tasks because no database environment was provided.

## cTrader and deployment status

A demo-only gateway boundary exists; no cTrader SDK/runtime or credentials were required. Platform-specific order mapping, position ownership metadata, reconnection/reconciliation, persisted shutdown recovery and demo forward testing require final human testing. Live mode is rejected by configuration validation.

## Known limitations and unresolved risks

- No empirical XAUUSD Strategy V1 or ML result exists.
- Indicator implementation is a deterministic research baseline and must be reconciled with broker/cTrader conventions.
- The backtester does not model gaps or order-book liquidity beyond configured costs.
- PostgreSQL migrations and resilience need environment testing.
- The cTrader gateway is an interface boundary, not a locally executed platform adapter.
- Persistent emergency state, alerting and structured production log sinks require deployment integration.
- Real losses can exceed planned risk because of gaps, slippage, liquidity and platform failures.

## Recommended next steps

1. Review and apply an EF Core initial migration against a development PostgreSQL database.
2. Implement the cTrader demo gateway in the platform runtime and validate symbol volume/tick semantics.
3. Collect trustworthy bid/ask XAUUSD history and run development/validation/locked-OOS research.
4. Run Shadow mode, then a meaningful demo forward test with reconciliation and operational monitoring.
5. Train/evaluate ML only after a leakage-reviewed labeled dataset exists; leave it disabled if it worsens the deterministic baseline.
6. Do not consider live trading without a separate human risk and deployment approval.
