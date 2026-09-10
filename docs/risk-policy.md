# GoldAiTrader — Risk Management Policy

## 1. Purpose

This document defines the mandatory risk-management rules for GoldAiTrader.

The Risk Engine has final authority over whether a trade may be opened, modified, or continued.

A strategy signal, machine-learning prediction, or high-confidence setup must never bypass the Risk Engine.

The primary objective is capital preservation and controlled exposure.

Profitability is not guaranteed.

---

## 2. Risk Principles

GoldAiTrader must follow these principles:

* protect capital before seeking profit;
* risk must be known before entry;
* every trade must have defined downside protection;
* position size must be calculated from risk;
* losses must never trigger automatic aggressive position-size increases;
* excessive drawdown must reduce or stop trading;
* unsuitable market conditions may result in no trade;
* risk controls must remain active during backtesting, demo trading, and eventual live validation.

`WAIT` and `REJECT` are valid trading decisions.

---

## 3. Default Risk Configuration

Initial research defaults:

| Setting                                |    Default |
| -------------------------------------- | ---------: |
| Risk per trade                         |      0.50% |
| Maximum simultaneous XAUUSD positions  |          1 |
| Daily loss warning                     |      1.50% |
| Daily hard loss limit                  |      2.00% |
| Weekly hard loss limit                 |      4.00% |
| Drawdown warning                       |      8.00% |
| Hard drawdown stop                     |     12.00% |
| Stop loss required                     |        Yes |
| Risk-based position sizing             |        Yes |
| Martingale                             | Prohibited |
| Unlimited grid                         | Prohibited |
| Averaging down without predefined risk | Prohibited |

All values must eventually be configurable.

These values are engineering controls and must not be represented as guarantees that actual losses cannot exceed them.

Real execution may be affected by:

* gaps;
* slippage;
* extreme volatility;
* broker execution;
* connection loss;
* liquidity conditions;
* platform failures.

---

## 4. Risk Per Trade

The initial default risk per trade is:

`0.50% of current account equity`

Example:

Account equity:

`$1,000`

Risk:

`0.50%`

Maximum planned risk:

`$5`

The bot must calculate position size based on this maximum planned loss.

Position size must not be chosen primarily from a fixed lot-size setting.

---

## 5. Position Sizing

Position size must consider:

* current account equity;
* configured risk percentage;
* entry price;
* stop-loss distance;
* XAUUSD contract specification;
* tick size;
* tick value;
* broker minimum volume;
* broker maximum volume;
* volume step.

Conceptually:

`Risk Amount = Account Equity × Risk Percentage`

The required position size must then be derived so that the expected loss at the initial stop does not exceed the permitted risk amount under normal execution assumptions.

The calculated volume must be normalized to the broker-supported volume step.

---

## 6. Position-Size Validation

Before approving a trade, validate that calculated volume:

* is greater than zero;
* meets broker minimum volume;
* does not exceed broker maximum volume;
* follows broker volume-step requirements;
* respects the configured risk limit;
* respects total account exposure;
* does not violate concurrent-position limits.

If valid volume cannot be calculated safely:

`REJECT TRADE`

Never automatically increase risk merely to satisfy the broker minimum lot size.

---

## 7. Stop-Loss Policy

Every normal strategy trade must have a defined stop loss.

The stop loss must be established before or as part of order submission where supported.

Trades must not intentionally run without downside protection simply to avoid realizing a loss.

The system must reject:

* zero stop distance;
* negative stop distance;
* invalid stop placement;
* stop distances violating broker requirements;
* calculations resulting in undefined risk.

---

## 8. Take-Profit Policy

Take profit may be:

* fixed;
* volatility based;
* structure based;
* risk/reward based;
* managed dynamically by an approved strategy.

Take-profit logic must not override hard risk protections.

A strategy may also support managed exits where justified and testable.

---

## 9. Maximum Simultaneous Positions

Initial default:

`1 active XAUUSD position`

The initial deterministic strategy must not open multiple overlapping Gold positions unless a later strategy specification explicitly introduces controlled multi-position behavior.

Increasing the number of simultaneous positions requires consideration of total portfolio risk.

Five positions risking 0.50% each must not accidentally become 2.50% uncontrolled exposure.

---

## 10. Total Exposure

The system must calculate aggregate open risk.

New trades must be rejected when opening them would cause total risk to exceed configured exposure limits.

Exposure calculation must include all positions controlled by GoldAiTrader.

Future multi-symbol expansion must account for correlated risk.

For the initial version:

`XAUUSD only`

---

## 11. Daily Loss Protection

Initial daily warning threshold:

`1.50%`

Initial daily hard stop:

`2.00%`

Daily loss should be measured using clearly defined account-equity or strategy-equity rules documented in implementation.

At the warning threshold, the Risk Engine may:

* reduce permitted risk;
* reject lower-quality setups;
* enter defensive mode.

At the hard threshold:

`NO NEW TRADES`

until the configured trading day resets.

Existing positions should be handled according to their predefined risk-management rules rather than closed blindly without considering execution consequences.

---

## 12. Weekly Loss Protection

Initial weekly hard loss limit:

`4.00%`

If reached:

* reject new trades;
* mark the strategy as risk-suspended;
* log the event;
* require the configured weekly reset before normal trading resumes.

The implementation must clearly define the timezone and start of the trading week.

---

## 13. Drawdown Calculation

Track at minimum:

* peak balance;
* current balance;
* peak equity;
* current equity;
* balance drawdown;
* equity drawdown.

Equity drawdown is particularly important because it captures unrealized losses.

Conceptually:

`Drawdown % = (Peak Equity - Current Equity) / Peak Equity × 100`

All calculations require automated tests.

---

## 14. Drawdown Warning

Initial warning:

`8.00%`

At or above the warning threshold, the system enters defensive mode.

Possible defensive actions include:

* reducing risk per trade;
* reducing trade frequency;
* requiring stronger strategy confirmation;
* preventing additional exposure;
* recording a risk alert.

The precise behavior must be deterministic and testable.

---

## 15. Hard Drawdown Stop

Initial hard drawdown limit:

`12.00%`

At or above this threshold:

`NEW TRADING MUST STOP`

The system must:

* reject new trade requests;
* record the shutdown reason;
* expose the current drawdown;
* require an explicit reset/recovery process.

An automated strategy must never silently bypass a drawdown shutdown.

---

## 16. Consecutive Loss Protection

The system must track consecutive losses.

A configurable threshold should allow the Risk Engine to:

* reduce risk;
* enter a cooldown;
* suspend trading temporarily.

Initial implementation should support this capability even if the final threshold is determined through research.

The bot must never respond to consecutive losses by increasing position size.

---

## 17. Martingale Prohibition

Martingale behavior is prohibited.

The following are not permitted:

* doubling lot size after a loss;
* progressively increasing exposure to recover losses;
* unlimited recovery sequences;
* sizing the next trade according to previous losses.

Example of prohibited logic:

`0.01 → loss → 0.02 → loss → 0.04 → loss → 0.08`

GoldAiTrader must not use this approach.

---

## 18. Grid Policy

Unlimited grid trading is prohibited.

Do not continuously add positions merely because price moves against an existing position.

Any future multi-entry strategy must:

* have a predefined maximum number of entries;
* have predefined total risk;
* calculate combined exposure before entry;
* have clear invalidation;
* remain within drawdown limits.

It must not function as disguised martingale recovery.

---

## 19. Averaging Down

Uncontrolled averaging down is prohibited.

A losing trade does not justify increasing risk.

Any future scaling strategy must be explicitly specified, bounded, backtested, and included in total-risk calculations.

---

## 20. Spread Protection

Before entry, the system must inspect the current spread.

Trades must be rejected when spread is outside the configured acceptable range.

Conceptually:

`Signal → Spread Check → PASS/REJECT`

Spread thresholds should eventually consider:

* normal XAUUSD conditions;
* session;
* volatility;
* broker characteristics.

Spread protection must be configurable and logged.

---

## 21. Slippage Protection

Execution logic should record expected and actual execution price.

Track:

`Slippage = Actual Fill Price - Requested/Expected Price`

Where supported, trades should be rejected or managed when expected execution quality is unacceptable.

Backtesting must not assume perfect fills.

---

## 22. Volatility Protection

Gold may experience rapid movements.

The system must support detection of abnormal volatility using measures such as:

* ATR;
* candle range;
* recent price movement;
* spread expansion.

Extreme volatility may result in:

`WAIT`

or:

`REJECT TRADE`

A high-volatility market must not automatically cause larger monetary risk.

---

## 23. Trading Session Controls

The system must support configurable trading sessions.

Potential initial research sessions include:

* London;
* New York;
* London/New York overlap.

Trades outside permitted sessions may be rejected.

Session rules must use an explicitly documented timezone.

---

## 24. News/Event Risk

The architecture should allow a future high-impact event filter.

Examples may include major releases affecting Gold and USD.

The first deterministic version may operate without an external news service if unavailable, but the design must allow such a filter to be added later without bypassing the Risk Engine.

---

## 25. Invalid Market Data

Reject trading decisions when required market data is:

* missing;
* stale;
* malformed;
* zero where impossible;
* NaN;
* infinite;
* chronologically invalid.

Risk calculations must fail safely.

Default behavior on uncertain input:

`DO NOT TRADE`

---

## 26. Connection and Broker Failures

Failures must not result in uncontrolled repeated order submissions.

Execution components should handle:

* timeouts;
* rejected orders;
* connection loss;
* invalid volume;
* invalid stops;
* market closure;
* insufficient margin;
* broker errors.

Retry logic must be bounded.

Never create an infinite order-submission loop.

---

## 27. Duplicate Trade Prevention

A single signal must not accidentally create multiple positions because of repeated callbacks or duplicate market events.

Trade requests should have enough state or identifiers to prevent accidental duplicate execution.

---

## 28. Emergency Shutdown

The architecture must support an emergency trading shutdown.

Possible causes include:

* hard drawdown reached;
* daily loss limit reached;
* corrupted market data;
* repeated execution errors;
* impossible account state;
* manual administrative shutdown.

During shutdown:

`NO NEW TRADES`

The shutdown state and reason must be logged.

---

## 29. Demo-First Policy

All broker integration must default to safe/demo behavior.

Simply starting GoldAiTrader must never be sufficient to risk real money.

Live trading must require explicit configuration outside normal autonomous development.

No contributor, automated workflow, or system component must ever activate live trading independently.

---

## 30. Backtesting Risk Integrity

The same fundamental risk logic used in trading must also be represented in backtesting.

Do not produce artificially attractive results by:

* ignoring spread;
* ignoring commission;
* ignoring slippage assumptions;
* ignoring stop-loss behavior;
* using unlimited leverage;
* ignoring drawdown shutdowns;
* bypassing position sizing.

A strategy that becomes unattractive under realistic costs must be reported as such.

---

## 31. Machine Learning and Risk

The machine-learning layer does not control account risk.

ML may eventually:

* approve/reject valid strategy setups;
* assign probability/confidence;
* classify market regimes.

ML must not independently:

* override maximum risk;
* override drawdown limits;
* remove stop losses;
* exceed position limits;
* increase leverage;
* activate live trading.

Conceptually:

`Strategy → ML Filter → Risk Engine → Execution`

The Risk Engine remains the final gate.

---

## 32. Required Logging

For every trade decision, where applicable, log:

* timestamp;
* symbol;
* direction;
* strategy signal;
* rejection reason;
* account equity;
* risk percentage;
* monetary risk;
* calculated position size;
* stop distance;
* spread;
* current drawdown;
* daily loss state;
* weekly loss state;
* open exposure;
* final Risk Engine decision.

Do not log credentials.

---

## 33. Required Automated Tests

Risk tests must include at minimum:

* 0.50% risk calculation;
* different account equity values;
* position sizing;
* broker minimum volume;
* broker maximum volume;
* volume-step normalization;
* zero stop distance;
* negative stop distance;
* invalid prices;
* daily warning;
* daily hard stop;
* weekly hard stop;
* equity drawdown;
* balance drawdown;
* drawdown warning;
* hard drawdown shutdown;
* maximum open positions;
* maximum exposure;
* spread rejection;
* emergency shutdown;
* duplicate-order protection where applicable.

Boundary values must be tested.

Example:

If daily hard stop is `2.00%`, test:

* 1.99%;
* 2.00%;
* 2.01%.

---

## 34. Configuration

Risk limits must not be scattered as magic numbers throughout the code.

Use strongly typed configuration or domain policy objects.

Example conceptual configuration:

* RiskPerTradePercent
* DailyLossWarningPercent
* DailyLossLimitPercent
* WeeklyLossLimitPercent
* DrawdownWarningPercent
* HardDrawdownPercent
* MaxOpenPositions
* MaxTotalRiskPercent
* MaxSpread

Defaults belong in one clearly identifiable location.

---

## 35. Changes to Risk Policy

A code change must not silently weaken the risk policy.

Changes to major risk defaults should be:

* deliberate;
* documented;
* test-covered.

Backtest performance alone is not sufficient justification for weakening safety limits.

---

## 36. Final Principle

The following priority order applies:

1. capital preservation;
2. risk containment;
3. execution correctness;
4. strategy robustness;
5. profitability;
6. trade frequency.

When uncertain:

`DO NOT TRADE`
