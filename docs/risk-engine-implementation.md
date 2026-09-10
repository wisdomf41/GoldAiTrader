# Risk Engine Implementation Notes

`RiskOptions.Validate()` rejects inconsistent or unsafe percentages, thresholds, exposure,
spread, position-count, and consecutive-loss settings when `RiskEngine` is constructed.

Broker `MinStopDistance` may be zero. This does not permit a zero-distance trade stop:
`RiskEngine` independently requires `TradeSetup.StopDistance > 0` before position-sizing
division. Volume is always rounded down to the broker step and is rejected when the broker
minimum would exceed planned risk.

Daily and weekly hard stops and balance/equity drawdown hard stops reject at the configured
threshold (`>=`). Warning state also begins at its configured threshold. Total exposure accepts
an exact configured boundary and rejects values above it. Absolute spread validation remains in
Risk; the Strategy V1 market-cost filter additionally enforces `Spread / ATR <= 0.10`.

Consecutive closed losses are represented by the separate `RiskState` model and updated through
`RiskStateTracker`. The default third consecutive loss suspends new trades for four hours. The
cooldown never increases per-trade risk; a non-losing close resets the sequence. Deployment code
must persist/reconstruct this state so a process restart cannot bypass an active suspension.
