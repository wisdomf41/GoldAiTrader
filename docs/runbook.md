# GoldAiTrader Demo-Safe Runbook

## Supported modes

- Backtest: simulated historical execution.
- Shadow: decisions and risk assessments only; no broker orders.
- Demo: permitted only after explicit configuration and a reviewed cTrader demo gateway.
- Live: intentionally blocked.

## Start and validate

1. Run `dotnet restore GoldAiTrader.slnx`, `dotnet build GoldAiTrader.slnx`, and `dotnet test GoldAiTrader.slnx`.
2. Keep `ExecutionMode` at `Shadow` until the cTrader demo gateway has passed human integration testing.
3. Verify XAUUSD, M5 execution, M15 context, broker connectivity, fresh market data, symbol tick/volume specifications, and account state.
4. Confirm risk options and that emergency shutdown is clear.
5. Never place credentials in committed configuration.

## Operating checks

Inspect the operational status for mode, broker connectivity, last market-data timestamp, stale-data state, emergency shutdown, and whether new orders are accepted. Duplicate signals are rejected by signal identifier.

## Emergency response

Trigger emergency shutdown to block all new orders. Existing broker-side stop-loss and take-profit orders must remain intact. Investigate daily/weekly loss, drawdown, stale data, broker failures, and reconciliation before a controlled reset.

## Restart and reconciliation

A platform adapter must reconstruct bot-owned positions and risk state before accepting new demo orders. It must identify positions by strategy/instance metadata and must not manage unrelated positions.

## Human testing still required

The cTrader-specific gateway, demo account volume semantics, slippage, reconnect/reconciliation, platform lifecycle callbacks, persistent shutdown reconstruction, PostgreSQL migration application, and alert delivery require environment-specific testing. Live activation is outside this runbook.
