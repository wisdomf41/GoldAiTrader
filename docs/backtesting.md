# GoldAiTrader — Backtesting and Validation Specification

## 1. Purpose

This document defines how GoldAiTrader strategies must be historically tested, validated, compared, and reported.

The purpose of backtesting is not to manufacture an attractive equity curve.

The purpose is to determine whether a strategy demonstrates evidence of a repeatable trading edge after realistic costs, risk controls, and chronological validation.

Backtest profitability must never be treated as guaranteed future profitability.

---

## 2. Core Principles

Every GoldAiTrader backtest must prioritize:

* chronological correctness;
* realistic execution assumptions;
* reproducibility;
* risk-policy compliance;
* separation of development and evaluation data;
* protection against overfitting;
* transparent reporting of failures;
* realistic transaction costs.

A strategy that fails testing must be reported as failed.

---

## 3. Initial Instrument

Initial backtesting instrument:

`XAUUSD`

Strategy V1 is defined in:

`docs/strategy.md`

Initial execution timeframe:

`M5`

Context timeframe:

`M15`

---

## 4. Shared Trading Logic

Backtesting must reuse the same core:

* market calculations;
* Strategy V1 logic;
* Risk Engine;
* position sizing;
* stop-loss logic;
* take-profit logic;
* trade restrictions

used by demo execution wherever practical.

Do not maintain a special highly profitable version of strategy logic only for backtests.

Conceptually:

Historical Data
→ Market Engine
→ Strategy
→ Risk Engine
→ Simulated Execution
→ Metrics

---

## 5. No Look-Ahead Bias

At each simulated timestamp, only information available at that historical moment may be used.

Never use:

* future candles;
* future highs;
* future lows;
* future indicator values;
* future trade results;
* future spread information

to make an earlier decision.

Completed-bar strategy logic must remain completed-bar logic during backtesting.

---

## 6. Multi-Timeframe Chronology

Strategy V1 uses:

* M5 execution data;
* M15 context data.

The backtester must align these chronologically.

An M15 candle must not be considered complete until its historical close time has actually occurred.

Example:

At 10:07:

The M15 candle covering 10:00–10:15 is incomplete.

Its final close, high, low, EMA state, or ADX state must not be used yet.

This requirement is mandatory.

---

## 7. Historical Data Quality

Historical data should contain enough information to reproduce Strategy V1 as accurately as practical.

Preferred data includes:

* timestamp;
* open;
* high;
* low;
* close;
* bid/ask or spread where available;
* volume where useful.

If only OHLC data is available, limitations must be documented.

Do not silently claim tick-level accuracy from candle-only data.

---

## 8. Data Validation

Historical datasets must be checked for:

* missing timestamps;
* duplicate bars;
* invalid OHLC relationships;
* zero or negative prices;
* chronological disorder;
* unexpected gaps;
* timezone inconsistencies.

Invalid data should be:

* rejected;
* repaired only through a documented deterministic process;
* or explicitly excluded.

Do not silently fabricate market values.

---

## 9. Timezone

Historical timestamps must have a clearly defined timezone.

Preferred internal research standard:

`UTC`

Broker/server data must be normalized when necessary.

Session rules from Strategy V1 must use the same temporal reference.

---

## 10. Starting Capital

Backtests must use configurable starting capital.

Initial research default:

`$1,000`

Additional capital scenarios may later include:

* $500
* $2,500
* $5,000
* $10,000

The purpose is to verify that risk-based sizing behaves correctly at different equity levels.

Position sizing must continue to use percentage risk.

---

## 11. Leverage

Backtest performance must not depend on unrealistic leverage assumptions.

Leverage should be represented primarily through margin feasibility.

It must not change the strategy's configured monetary risk per trade.

A larger leverage setting must not automatically cause larger strategy risk.

---

## 12. Commission

Transaction costs must be represented.

Where the selected IC Markets account structure charges commission, use a configurable realistic commission model.

Commission should be applied to each completed trade according to the selected volume.

Never assume:

`Commission = 0`

unless explicitly running a special diagnostic test.

---

## 13. Spread

Spread must be represented.

Preferred approaches, in order:

1. historical bid/ask data;
2. historical spread data;
3. configurable dynamic spread model;
4. conservative fixed spread assumption.

Strategy V1 already contains spread filtering.

The simulated entry and exit process must also account for spread.

---

## 14. Slippage

Backtesting must not assume every order receives a perfect theoretical fill.

Implement configurable slippage assumptions.

Initial modes may include:

* zero slippage for diagnostics;
* normal slippage;
* adverse stress slippage.

The main reported backtest should use a realistic non-zero assumption when suitable data is unavailable.

---

## 15. BUY Execution

For BUY orders:

The simulated fill must use an executable ask-side concept where bid/ask data is available.

Stops and targets must respect realistic price-side execution rules.

Do not use mid-price execution if that would unrealistically improve results.

---

## 16. SELL Execution

For SELL orders:

The simulated fill must use an executable bid-side concept where bid/ask data is available.

Stops and targets must respect realistic executable pricing.

---

## 17. Entry Timing

Strategy V1 evaluates completed M5 bars.

When a signal becomes known at a bar close:

The backtester must not execute earlier inside that same bar using a favorable historical price.

Execution should occur at the next realistically available executable price.

---

## 18. Stop-Loss Execution

Stops must be evaluated using realistic historical movement.

If price moves through the stop between available observations, fill assumptions should be conservative.

Do not automatically award the exact stop price when a gap or severe movement would imply worse execution.

Any approximation must be documented.

---

## 19. Take-Profit Execution

Take-profit fills must also obey historical chronology.

If both stop and target appear to have been touched within the same candle and intrabar ordering is unknown:

Use a documented conservative policy.

Preferred approach:

* use higher-resolution data when available.

If unavailable:

* choose the outcome conservatively;
* or classify the trade as ambiguous and handle according to a predefined rule.

Do not automatically select the profitable outcome.

---

## 20. Maximum Holding Exit

Strategy V1 initially uses:

`24 M5 completed bars`

for maximum holding duration.

The backtester must exit using the next realistically available price after the limit is reached.

Record:

`TIME_EXIT`

---

## 21. Risk Engine

Historical trades must pass the actual Risk Engine.

Risk controls must not be disabled merely because this is a simulation.

The backtest must represent:

* risk per trade;
* maximum open positions;
* daily loss warning;
* daily hard stop;
* weekly loss protection;
* drawdown protection;
* spread protection;
* exposure limits;
* emergency shutdown.

---

## 22. Position Sizing

Position size must be recalculated from historical account equity.

Initial risk:

`0.50%`

Example:

Equity = $1,000

Risk = $5

After equity changes:

Position sizing must change accordingly.

Do not size all historical trades using the original starting balance.

---

## 23. Broker Volume Constraints

Simulation should support:

* minimum volume;
* maximum volume;
* volume step;
* contract size;
* tick size;
* tick value.

If the calculated trade size cannot meet broker minimum volume without exceeding risk:

`REJECT TRADE`

---

## 24. Daily Risk State

The backtester must maintain daily trading state.

Track:

* realized strategy P/L;
* relevant equity loss;
* trade count;
* warning state;
* hard-stop state.

Daily reset timezone must be explicit.

Initial standard:

`UTC`

unless later broker-specific requirements justify otherwise.

---

## 25. Weekly Risk State

The backtester must track weekly losses.

At the configured weekly hard threshold:

`NO NEW TRADES`

until the next configured trading week.

---

## 26. Equity Curve

Record account equity throughout the test.

Equity must include unrealized P/L where simulation resolution permits.

The system should produce:

* balance curve;
* equity curve;
* peak equity;
* drawdown series.

---

## 27. Maximum Drawdown

At minimum report:

* maximum balance drawdown percentage;
* maximum equity drawdown percentage;
* maximum drawdown currency amount.

Primary research emphasis:

`Maximum Equity Drawdown`

because floating losses matter.

---

## 28. Required Trade Records

Every simulated trade should record at least:

* TradeId
* StrategyVersion
* Symbol
* Direction
* SignalTime
* EntryTime
* EntryPrice
* PositionSize
* StopLoss
* TakeProfit
* InitialRiskAmount
* InitialRiskPercent
* ExitTime
* ExitPrice
* ExitReason
* GrossProfitLoss
* Commission
* SlippageCost where modeled
* NetProfitLoss
* RMultiple
* HoldingDuration
* EquityBefore
* EquityAfter

---

## 29. Required Decision Records

Rejected setups are valuable research data.

Record relevant rejected decisions including:

* timestamp;
* candidate direction;
* reason;
* spread;
* volatility;
* drawdown state;
* daily loss state;
* existing position state.

This later helps ML research distinguish:

* traded setups;
* rejected setups;
* missed opportunities.

---

## 30. Core Performance Metrics

Every main backtest must report:

* starting balance;
* ending balance;
* net profit/loss;
* net return percentage;
* gross profit;
* gross loss;
* total trades;
* profitable trades;
* losing trades;
* break-even trades;
* win rate;
* loss rate;
* profit factor;
* expectancy;
* average win;
* average loss;
* payoff ratio;
* average R;
* maximum consecutive wins;
* maximum consecutive losses;
* maximum balance drawdown;
* maximum equity drawdown;
* recovery factor;
* average holding duration;
* trades per day;
* trades per month.

---

## 31. Risk-Adjusted Metrics

Where mathematically appropriate, also calculate:

* Sharpe ratio;
* Sortino ratio.

Document:

* return sampling interval;
* risk-free-rate assumption;
* annualization assumption.

Do not display mathematically meaningless Sharpe/Sortino values without explanation.

---

## 32. Exit Statistics

Report counts and performance for:

* STOP_LOSS
* TAKE_PROFIT
* TIME_EXIT
* RISK_EXIT
* EMERGENCY_EXIT
* other explicitly defined exits.

This helps identify whether Strategy V1 behaves as designed.

---

## 33. Direction Statistics

Report BUY and SELL separately.

For each direction include:

* trade count;
* net P/L;
* win rate;
* profit factor;
* expectancy;
* maximum losing streak where practical.

A strategy may appear acceptable overall while one side performs poorly.

---

## 34. Monthly Analysis

Provide monthly results when the test duration permits.

Track:

* monthly return;
* trades;
* winners;
* losers;
* drawdown.

Do not judge a multi-year strategy from total profit alone.

We need to understand consistency through different periods.

---

## 35. Market-Regime Analysis

Where possible, analyze performance according to relevant regime characteristics.

Examples:

* stronger trend;
* weaker trend;
* higher volatility;
* lower volatility;
* London hours;
* New York hours;
* overlap hours.

This helps determine whether the strategy depends on one narrow environment.

---

## 36. Development / Validation / Out-of-Sample Separation

Historical testing must be chronologically separated.

Use three conceptual partitions:

### Development

Used to:

* implement strategy;
* debug;
* research broad parameter behavior.

### Validation

Used to compare reasonable strategy variants and parameter choices.

### Final Out-of-Sample

Reserved for final evaluation.

Do not repeatedly tune strategy rules after inspecting final out-of-sample results.

---

## 37. Date Selection

Exact date ranges depend on reliable historical XAUUSD data availability.

Once data availability is known, document the chosen ranges before final optimization.

Example only:

Development:

`2019–2023`

Validation:

`2024`

Final out-of-sample:

`2025–2026`

This example is not mandatory.

The actual ranges must be chosen based on available trustworthy data and sufficient market diversity.

---

## 38. Final Test Lock

Once a historical period is designated as:

`FINAL OUT-OF-SAMPLE`

do not repeatedly inspect it while changing the strategy.

If the strategy is materially modified after seeing final results:

A new untouched evaluation period should be used where possible.

---

## 39. Walk-Forward Testing

After basic deterministic validation works, implement walk-forward evaluation.

Conceptually:

Window 1:

Train/develop on earlier period
→ evaluate on next period

Window 2:

Move window forward
→ evaluate next unseen period

Repeat.

This helps determine whether the system's behavior persists through time.

---

## 40. Parameter Optimization

Optimization is permitted only after the basic strategy and backtester are functioning.

Do not optimize hundreds of parameters simultaneously.

Prefer a small number of economically/trading-meaningful parameters.

Examples:

* MinimumAdx
* PullbackAtrTolerance
* StopAtrMultiplier
* RewardRiskRatio
* RSI ranges

---

## 41. Optimization Objective

Do not optimize solely for:

`Maximum Net Profit`

Use a multi-objective view including:

* positive expectancy;
* acceptable drawdown;
* adequate trade count;
* profit factor;
* parameter stability;
* out-of-sample performance.

---

## 42. Parameter Stability

Prefer broad parameter regions.

Example:

Acceptable:

ADX 18 → reasonable
ADX 20 → reasonable
ADX 22 → reasonable
ADX 24 → reasonable

Suspicious:

ADX 21.37 → excellent
ADX 21.30 → terrible
ADX 21.45 → terrible

An isolated historical optimum may indicate overfitting.

---

## 43. Minimum Trade Sample

Do not treat a strategy with a tiny number of trades as statistically convincing.

The final report must include total sample size.

If sample size is too small:

Report insufficient evidence.

Do not hide this limitation.

---

## 44. Extreme Trade Dependency

Measure whether performance depends heavily on a few exceptional winners.

Report where practical:

* largest winning trade;
* largest losing trade;
* percentage of net profit attributable to top 1 trade;
* top 5 trades;
* top 10 trades.

A strategy whose entire profitability disappears when one unusual trade is removed should be treated cautiously.

---

## 45. Stress Test — Increased Costs

Run a transaction-cost stress scenario.

For example:

* increase commission;
* increase spread;
* increase slippage.

A robust strategy should not immediately collapse under a modest increase in realistic costs.

Record results separately.

---

## 46. Stress Test — Slippage

Run at least:

* normal slippage assumption;
* adverse slippage assumption.

Compare:

* net return;
* profit factor;
* drawdown;
* expectancy.

---

## 47. Stress Test — Spread

Test wider spreads than the baseline.

This is particularly important for XAUUSD during volatile periods.

Do not assume the broker always provides ideal spread conditions.

---

## 48. Stress Test — Parameter Perturbation

Slightly vary key parameters.

Examples:

RewardRisk:

* 1.8
* 2.0
* 2.2

StopATR:

* 1.3
* 1.5
* 1.7

MinimumADX:

* 18
* 20
* 22

The strategy should not depend on one exact parameter combination.

---

## 49. Stress Test — Starting Date

Where possible, repeat evaluations using different historical start points.

This helps detect strategies whose outcome depends heavily on beginning immediately before a favorable regime.

---

## 50. Risk Stress Testing

Test risk engine boundaries including:

* daily hard stop;
* weekly hard stop;
* drawdown warning;
* hard drawdown stop;
* maximum open positions;
* minimum volume rejection.

Confirm through automated tests and historical simulation.

---

## 51. Backtest Determinism

Given:

* same dataset;
* same strategy version;
* same parameters;
* same execution assumptions;
* same random seed if randomness exists;

the backtest should reproduce the same result.

Record the configuration used for each research run.

---

## 52. Backtest Run Identity

Each significant research run should have an identifier.

Record:

* run ID;
* strategy version;
* code version/commit where practical;
* dataset;
* date range;
* parameter values;
* starting equity;
* cost model;
* timestamp;
* result summary.

---

## 53. Backtest Configuration

Use strongly typed configuration.

Possible sections:

BacktestOptions

* StartDate
* EndDate
* StartingBalance
* CommissionModel
* SpreadModel
* SlippageModel
* BrokerVolumeRules
* Timezone
* DataSource

Strategy options and Risk options must be separately represented.

---

## 54. Simulated Execution Abstraction

Use an execution abstraction rather than embedding historical trade fills directly into strategy logic.

Conceptually:

`ITradeExecutor`

Implementations:

`SimulatedTradeExecutor`

Later:

`CTraderTradeExecutor`

This helps keep the strategy consistent between research and demo operation.

---

## 55. Performance Metrics Component

Performance calculations should be isolated and unit tested.

Conceptually:

`PerformanceAnalyzer`

Responsibilities may include:

* net return;
* profit factor;
* expectancy;
* drawdown;
* consecutive losses;
* R statistics;
* Sharpe;
* Sortino;
* monthly results.

Do not scatter metric calculations across unrelated classes.

---

## 56. Unit Tests

Automated backtesting tests should include:

* correct chronological bar processing;
* M15 incomplete bar cannot leak into M5 decision;
* spread applied correctly;
* commission applied correctly;
* BUY fill calculation;
* SELL fill calculation;
* stop execution;
* target execution;
* same-bar ambiguous stop/target behavior;
* time exit;
* position-size equity updates;
* daily loss state;
* weekly state;
* drawdown state;
* deterministic repeated run;
* metric calculations.

---

## 57. Integration Tests

Where practical, create small deterministic historical scenarios.

Example synthetic dataset:

* clear bullish trend;
* pullback;
* confirmation;
* BUY entry;
* target reached.

Expected sequence:

`WAIT → WAIT → BUY candidate → Risk Approved → Trade → TAKE_PROFIT`

Also create losing and rejected scenarios.

These tests are more valuable than depending solely on years of broker history during development.

---

## 58. Reporting Failed Tests

If Strategy V1 performs poorly:

Report:

`STRATEGY VALIDATION FAILED`

Include the actual results.

Do not automatically modify the strategy until it passes.

Any new Strategy V2 should be identified as a new research version.

---

## 59. Strategy Versions

Backtesting must preserve strategy version identity.

Initial version:

`TrendPullbackV1`

If rules materially change:

Use a new version, for example:

`TrendPullbackV2`

Do not overwrite the historical identity of previous experiments.

---

## 60. Benchmark

Where useful, compare the strategy against simple baselines.

Potential baselines:

* no trading;
* simple trend-only rule;
* random-entry simulation with equivalent risk constraints where scientifically appropriate.

The purpose is to determine whether complexity actually adds value.

---

## 61. Reporting Format

Each serious evaluation should generate a machine-readable result and a readable summary.

Possible machine-readable format:

JSON

Readable format:

Markdown report.

The final project report will summarize major evaluations in:

`docs/final-report.md`

---

## 62. Charts

Future reporting may include:

* equity curve;
* balance curve;
* drawdown curve;
* monthly returns;
* trade distribution.

Charts are useful diagnostics but must not replace numerical metrics.

---

## 63. Transaction-Cost Transparency

Every result report must state:

* commission assumption;
* spread assumption;
* slippage assumption.

Never present a backtest result without making these assumptions available.

---

## 64. Data Limitations

If data has known weaknesses, document them.

Examples:

* missing true historical spread;
* candle-only data;
* incomplete tick history;
* timezone conversions;
* broker-specific price differences.

Do not imply precision beyond what the data supports.

---

## 65. Success Criteria

A strategy is not validated merely by exceeding one numerical threshold.

Evidence should include:

* positive out-of-sample expectancy;
* reasonable profit factor;
* controlled drawdown;
* adequate trade count;
* parameter stability;
* reasonable transaction-cost robustness;
* no look-ahead bias;
* no risk-policy violations.

---

## 66. Initial Drawdown Research Target

Strategy V1 should aim for:

`Maximum Equity Drawdown <= 12%`

because the research Risk Engine hard-stop is initially 12%.

This is a target and system constraint, not a guarantee.

Lower drawdown is preferable.

---

## 67. Profit Factor

Profit factor:

`Gross Profit / Absolute Gross Loss`

A value above 1 means historical gross profits exceeded historical gross losses.

Do not judge the system solely by this metric.

The final report should include it alongside drawdown, expectancy, and sample size.

---

## 68. Expectancy

Calculate average expected net outcome per trade.

Conceptually:

`Expectancy = (WinRate × AverageWin) - (LossRate × AverageLoss)`

Use consistent sign conventions.

Also consider expectancy expressed in R.

---

## 69. R-Multiple

For each trade:

`R = Net Trade Result / Initial Planned Risk`

Examples:

Full stop:

approximately `-1R`, subject to slippage/costs.

2R target:

approximately `+2R`, less costs.

R-normalization helps compare trades with changing equity and position size.

---

## 70. Recovery Factor

Where appropriate:

`Recovery Factor = Net Profit / Maximum Drawdown`

Specify whether drawdown is measured in currency or percentage consistently with the calculation.

---

## 71. Sharpe / Sortino Limitations

Sharpe and Sortino must not be used deceptively.

Document:

* calculation method;
* frequency;
* annualization;
* treatment of no-trade periods.

Trading strategies with irregular returns require careful interpretation.

---

## 72. Continuous Development Tests vs Final Evaluation

Developers may continuously run:

* unit tests;
* integration tests;
* development backtests.

Contributors must not continuously optimize against the locked final out-of-sample period.

This distinction is mandatory.

---

## 73. Development Workflow Rules

During development, contributors may:

* implement backtesting infrastructure;
* create synthetic test data;
* fix calculation bugs;
* improve test coverage;
* run development backtests;
* report failed research results.

Contributors must not:

* fabricate profitable results;
* alter historical data to improve performance;
* weaken risk controls for profit;
* repeatedly optimize final out-of-sample data;
* claim profitability guarantees.

---

## 74. Final Evaluation Status

The final report must clearly distinguish:

`BACKTESTING SYSTEM COMPLETE`

from:

`STRATEGY VALIDATED`

It is entirely acceptable to have:

`BACKTESTING SYSTEM COMPLETE`

and:

`STRATEGY VALIDATION FAILED`

---

## 75. Core Principle

Backtesting exists to challenge the strategy, not to prove it correct.

When the evidence contradicts the strategy hypothesis, the evidence wins.
