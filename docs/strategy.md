# GoldAiTrader — Strategy Specification

## 1. Purpose

This document defines the first deterministic trading strategy for GoldAiTrader.

Strategy V1 is designed primarily to:

* provide a clear and testable XAUUSD strategy;
* produce structured historical trade data;
* exercise the Risk Engine;
* support realistic backtesting;
* provide a baseline for later machine-learning filtering;
* avoid unnecessary complexity.

Strategy V1 must be implemented and evaluated before machine learning is introduced.

Profitability is not assumed or guaranteed.

---

## 2. Instrument

Initial instrument:

`XAUUSD`

No other instruments are part of Strategy V1.

The architecture may support future instruments, but Strategy V1 research must remain focused on Gold.

---

## 3. Strategy Type

Strategy V1 is a:

**Trend + Pullback + Momentum Confirmation strategy**

Conceptually:

`Trend → Pullback → Confirmation → Risk Check → Trade`

The strategy should avoid attempting to predict every market movement.

It should trade selectively.

`WAIT` is a valid and expected outcome.

---

## 4. Timeframes

Primary execution timeframe:

`M5`

Higher-timeframe market context:

`M15`

Initial design:

* M15 determines primary trend/regime context.
* M5 determines pullback and entry timing.

Do not use incomplete future candles.

Only information available at the decision timestamp may be used.

---

## 5. Closed-Bar Decisions

Strategy V1 should make normal entry decisions using completed candles.

Do not make historical decisions using information from a candle that had not yet closed.

For example:

At the close of M5 candle N:

* candle N may be evaluated;
* candle N+1 does not yet exist;
* future high/low information must not be used.

This requirement applies to both backtesting and demo execution.

---

## 6. Primary Indicators

Initial indicators:

### M15

* EMA 50
* EMA 200
* ADX 14

### M5

* EMA 20
* EMA 50
* RSI 14
* ATR 14

Indicator periods must be configurable.

Do not add additional indicators without a documented research reason.

---

## 7. Trend Classification

### Bullish Trend

Initial bullish regime requires:

`M15 EMA50 > M15 EMA200`

and:

`EMA50 slope > 0`

and:

`ADX >= configured minimum`

Initial research default:

`Minimum ADX = 20`

### Bearish Trend

Initial bearish regime requires:

`M15 EMA50 < M15 EMA200`

and:

`EMA50 slope < 0`

and:

`ADX >= configured minimum`

### Otherwise

Market regime:

`NO TREND / WAIT`

Strategy V1 should not force trades in unclear conditions.

---

## 8. EMA Slope

EMA slope must be calculated using historical completed values.

Conceptually:

`Slope = Current EMA50 - EMA50 N bars ago`

Initial research default:

`Slope lookback = 3 completed M15 bars`

Bullish:

`Slope > 0`

Bearish:

`Slope < 0`

The slope calculation must not use future data.

---

## 9. Pullback Concept

The strategy does not enter simply because a trend exists.

It waits for price to pull back toward a short-term value area on M5.

Initial value area:

`M5 EMA20`

with optional relationship to:

`M5 EMA50`

The pullback should occur without invalidating the higher-timeframe trend.

---

## 10. Bullish Pullback

For a potential BUY setup:

1. M15 regime must be bullish.
2. Price must pull back toward M5 EMA20.
3. Price must remain structurally compatible with the bullish setup.
4. Entry confirmation is required.

A configurable ATR-based tolerance should define what counts as "near EMA20".

Conceptually:

`Distance from EMA20 <= ATR × PullbackTolerance`

Initial research default:

`PullbackTolerance = 0.30 ATR`

---

## 11. Bearish Pullback

For a potential SELL setup:

1. M15 regime must be bearish.
2. Price must pull back toward M5 EMA20.
3. Price must remain structurally compatible with the bearish setup.
4. Entry confirmation is required.

Use the same configurable ATR-based pullback concept.

---

## 12. BUY Confirmation

After a valid bullish pullback, a BUY candidate requires a completed M5 confirmation candle.

Initial conditions:

* M15 trend is bullish;
* ADX requirement passes;
* M5 price has entered the pullback zone;
* confirmation candle closes bullish;
* confirmation candle closes above M5 EMA20;
* M5 EMA20 is above M5 EMA50;
* RSI is above the bullish minimum;
* RSI is below the configured overextended limit.

Initial RSI research range:

`52 <= RSI <= 68`

If all strategy conditions are not satisfied:

`WAIT`

---

## 13. SELL Confirmation

After a valid bearish pullback, a SELL candidate requires:

* M15 trend is bearish;
* ADX requirement passes;
* M5 price has entered the pullback zone;
* confirmation candle closes bearish;
* confirmation candle closes below M5 EMA20;
* M5 EMA20 is below M5 EMA50;
* RSI is below the bearish maximum;
* RSI remains above the configured oversold limit.

Initial RSI research range:

`32 <= RSI <= 48`

If all strategy conditions are not satisfied:

`WAIT`

---

## 14. Candle Direction

Bullish candle:

`Close > Open`

Bearish candle:

`Close < Open`

A doji or effectively flat candle should not qualify as confirmation unless later research explicitly introduces such behavior.

---

## 15. Entry Timing

Strategy evaluation occurs after the confirmation candle closes.

A valid approved setup should target execution using the next available executable market price.

Backtesting must not assume entry at a historical price that was unavailable after the signal became known.

Record:

* signal timestamp;
* expected entry;
* simulated/actual execution price;
* slippage.

---

## 16. Session Filter

Strategy V1 should support a configurable trading window.

Initial research window:

`07:00 UTC – 17:00 UTC`

The purpose is to concentrate research around active European and US trading hours.

This is a research configuration, not a claim that all hours inside the window are profitable.

The architecture should later allow more detailed London/New York session definitions.

Timezone must always be explicit.

---

## 17. No New Trades Outside Session

Outside the configured trading session:

`WAIT`

Existing positions may continue to be managed according to their predefined exit rules.

Do not automatically close an existing trade merely because the entry session has ended unless the exit policy explicitly requires it.

---

## 18. Spread Filter

Strategy candidates must pass the Risk Engine spread check.

A spread filter must not rely only on a hardcoded absolute Gold spread.

Initial research should support:

`Spread / ATR ratio`

Conceptually:

`SpreadRatio = CurrentSpread / ATR`

Initial research default maximum:

`0.10`

Meaning spread should generally not exceed 10% of current M5 ATR.

This value must be configurable and validated through testing.

---

## 19. Volatility Filter

Strategy V1 should reject abnormal volatility conditions.

Use ATR relative to price.

Conceptually:

`AtrPercent = ATR / Price × 100`

Initial research defaults:

Minimum ATR percentage:

`0.03%`

Maximum ATR percentage:

`0.50%`

Below minimum:

`WAIT — insufficient movement`

Above maximum:

`WAIT — abnormal volatility`

These values are research starting points and must be configurable.

---

## 20. Position Limit

Strategy V1 allows:

`Maximum 1 active XAUUSD position`

No pyramiding.

No overlapping Strategy V1 positions.

No grid.

No martingale.

No averaging into losing positions.

The Risk Engine retains final authority.

---

## 21. Risk Per Trade

Initial default:

`0.50% of current account equity`

Position size must be calculated by the Risk Engine.

Strategy code must not independently choose arbitrary lot sizes.

Detailed requirements are defined in:

`docs/risk-policy.md`

---

## 22. Stop-Loss Design

Strategy V1 uses volatility and price structure for stop placement.

For BUY:

Stop should be below the relevant recent swing low with a volatility buffer.

For SELL:

Stop should be above the relevant recent swing high with a volatility buffer.

The final stop distance must also satisfy a minimum ATR-based distance.

Conceptually:

`Minimum Stop Distance = ATR × StopAtrMultiplier`

Initial research default:

`StopAtrMultiplier = 1.5`

The safer/wider valid stop between structure and minimum ATR distance should be used.

---

## 23. Swing Definition

Initial research swing lookback:

`5 completed M5 bars`

For BUY:

Use the lowest valid low in the configured lookback.

For SELL:

Use the highest valid high in the configured lookback.

The current signal calculation must not inspect future bars.

Swing lookback must be configurable.

---

## 24. Stop Buffer

A small configurable volatility buffer should be applied beyond the structural swing.

Conceptually:

`Buffer = ATR × SwingBufferMultiplier`

Initial research default:

`SwingBufferMultiplier = 0.10`

BUY:

`Stop below swing low - buffer`

SELL:

`Stop above swing high + buffer`

---

## 25. Take Profit

Initial Strategy V1 take profit:

`2R`

where:

`R = initial monetary/price risk`

Conceptually:

BUY:

`Target = Entry + (Entry - Stop) × 2`

SELL:

`Target = Entry - (Stop - Entry) × 2`

Initial reward-to-risk ratio:

`2.0`

This must be configurable.

---

## 26. Initial Exit Simplicity

Strategy V1 should initially avoid complex exit optimization.

Primary exits:

* stop loss;
* take profit;
* maximum holding period;
* emergency Risk Engine action where necessary.

Do not initially add:

* aggressive trailing stops;
* multiple partial exits;
* recovery exits;
* martingale recovery;
* arbitrary discretionary exit rules.

These may be researched later.

---

## 27. Maximum Holding Period

Initial maximum holding period:

`24 completed M5 bars`

Equivalent to approximately:

`2 hours`

If neither stop nor target has been reached by then:

Exit at the next realistically executable price.

Record exit reason:

`TIME_EXIT`

This setting must be configurable.

---

## 28. Cooldown

After a position closes, Strategy V1 should wait before opening another position.

Initial cooldown:

`3 completed M5 bars`

Equivalent to approximately:

`15 minutes`

Purpose:

* reduce immediate re-entry noise;
* prevent rapid repeated entries around the same signal;
* simplify initial analysis.

Cooldown must be configurable.

---

## 29. Maximum Trades Per Day

Initial research limit:

`4 completed entries per trading day`

The purpose is to prevent accidental overtrading during early research.

This is separate from the daily loss limit.

If maximum trade count is reached:

`WAIT`

until the configured next trading day.

---

## 30. BUY Decision Pipeline

Conceptual BUY pipeline:

`New completed M5 bar`

↓

`Trading session valid?`

↓

`Market data valid?`

↓

`M15 EMA50 > EMA200?`

↓

`M15 EMA50 slope positive?`

↓

`ADX >= minimum?`

↓

`M5 EMA20 > EMA50?`

↓

`Valid pullback near EMA20?`

↓

`Bullish confirmation candle?`

↓

`RSI within bullish range?`

↓

`Volatility acceptable?`

↓

`Spread acceptable?`

↓

`Cooldown complete?`

↓

`Daily trade count available?`

↓

`No existing XAUUSD position?`

↓

`Create BUY candidate`

↓

`Risk Engine`

↓

`APPROVE or REJECT`

↓

`Execution`

---

## 31. SELL Decision Pipeline

Conceptual SELL pipeline:

`New completed M5 bar`

↓

`Trading session valid?`

↓

`Market data valid?`

↓

`M15 EMA50 < EMA200?`

↓

`M15 EMA50 slope negative?`

↓

`ADX >= minimum?`

↓

`M5 EMA20 < EMA50?`

↓

`Valid pullback near EMA20?`

↓

`Bearish confirmation candle?`

↓

`RSI within bearish range?`

↓

`Volatility acceptable?`

↓

`Spread acceptable?`

↓

`Cooldown complete?`

↓

`Daily trade count available?`

↓

`No existing XAUUSD position?`

↓

`Create SELL candidate`

↓

`Risk Engine`

↓

`APPROVE or REJECT`

↓

`Execution`

---

## 32. WAIT Reasons

Strategy V1 should expose useful WAIT/rejection reasons.

Examples:

* OutsideTradingSession
* NoTrend
* WeakTrend
* NoPullback
* NoConfirmation
* RsiNotValid
* SpreadTooHigh
* VolatilityTooLow
* VolatilityTooHigh
* ExistingPosition
* CooldownActive
* DailyTradeLimitReached
* RiskRejected
* InvalidMarketData

This improves debugging and later ML dataset analysis.

---

## 33. No Signal Repainting

Historical signals must not change because future candles become available.

The backtester must reproduce decisions based only on information known at the historical timestamp.

Any implementation that effectively repaints past signals is invalid.

---

## 34. Duplicate Entry Protection

The same completed signal candle must not create multiple entries.

Each evaluated signal should have a unique or reproducible identity based on information such as:

* symbol;
* timeframe;
* signal candle timestamp;
* strategy version;
* direction.

Execution must prevent accidental duplicate orders.

---

## 35. Strategy Versioning

Strategy V1 must have an identifiable version.

Initial version:

`TrendPullbackV1`

Trade records should eventually include:

`StrategyVersion`

This allows later comparisons against:

* V2;
* ML-filtered versions;
* alternate parameter sets.

---

## 36. Configuration

Initial Strategy V1 parameters should be represented using strongly typed configuration.

Potential settings:

* ExecutionTimeframe
* ContextTimeframe
* FastTrendEmaPeriod
* SlowTrendEmaPeriod
* EntryEmaPeriod
* EntrySlowEmaPeriod
* AdxPeriod
* MinimumAdx
* AtrPeriod
* RsiPeriod
* BullishRsiMinimum
* BullishRsiMaximum
* BearishRsiMinimum
* BearishRsiMaximum
* PullbackAtrTolerance
* StopAtrMultiplier
* SwingLookbackBars
* SwingBufferMultiplier
* RewardRiskRatio
* MaximumHoldingBars
* CooldownBars
* MaximumTradesPerDay
* TradingSessionStartUtc
* TradingSessionEndUtc
* MaximumSpreadAtrRatio
* MinimumAtrPercent
* MaximumAtrPercent

Avoid scattering these values throughout strategy code.

---

## 37. Default Research Parameters

Initial defaults:

| Parameter           |           Value |
| ------------------- | --------------: |
| Execution timeframe |              M5 |
| Context timeframe   |             M15 |
| M15 fast EMA        |              50 |
| M15 slow EMA        |             200 |
| M5 fast EMA         |              20 |
| M5 slow EMA         |              50 |
| ADX period          |              14 |
| Minimum ADX         |              20 |
| RSI period          |              14 |
| Bullish RSI         |           52–68 |
| Bearish RSI         |           32–48 |
| ATR period          |              14 |
| Pullback tolerance  |        0.30 ATR |
| Minimum stop        |        1.50 ATR |
| Swing lookback      |          5 bars |
| Swing buffer        |        0.10 ATR |
| Reward/Risk         |             2.0 |
| Maximum holding     |      24 M5 bars |
| Cooldown            |       3 M5 bars |
| Maximum trades/day  |               4 |
| Entry session       | 07:00–17:00 UTC |
| Maximum spread/ATR  |            0.10 |
| Minimum ATR %       |           0.03% |
| Maximum ATR %       |           0.50% |

These are research starting values.

They are not claimed to be optimal.

---

## 38. Optimization Rules

Do not optimize Strategy V1 solely for maximum historical profit.

When parameter optimization is eventually performed:

Prefer broad stable regions.

Example:

Better:

* ADX 18 acceptable
* ADX 20 acceptable
* ADX 22 acceptable
* ADX 24 acceptable

Suspicious:

* ADX 21.37 extremely profitable
* nearby values fail badly

Stable behavior is preferred over one isolated historical optimum.

---

## 39. Training/Development Data

The initial deterministic strategy does not require ML training.

However, strategy development data must still be separated from final evaluation data.

Do not repeatedly inspect final out-of-sample results while changing Strategy V1.

Specific historical date ranges will be defined in the backtesting specification based on available reliable data.

---

## 40. Backtesting Expectations

Backtesting must include realistic assumptions for:

* spread;
* commission;
* slippage;
* stop execution;
* take-profit execution;
* position sizing;
* daily limits;
* drawdown limits;
* session restrictions.

Do not backtest Strategy V1 assuming zero transaction cost.

---

## 41. Required Strategy Metrics

At minimum evaluate:

* total trades;
* BUY trades;
* SELL trades;
* net return;
* maximum equity drawdown;
* profit factor;
* expectancy;
* win rate;
* average win;
* average loss;
* reward/risk realized;
* consecutive losses;
* trades per month;
* average holding period;
* stop-loss exits;
* target exits;
* time exits;
* Risk Engine rejections.

---

## 42. Strategy Validation

Strategy V1 must not be labelled validated merely because the development backtest is profitable.

Validation requires:

* realistic transaction costs;
* chronological correctness;
* no look-ahead bias;
* no future leakage;
* risk-policy compliance;
* separate out-of-sample evaluation;
* acceptable behavior across different market conditions.

---

## 43. Provisional Research Goals

These are research targets, not guarantees:

* positive out-of-sample expectancy;
* profit factor meaningfully above 1.0;
* controlled maximum drawdown;
* stable behavior across parameter neighborhoods;
* sufficient trade sample size;
* no dependence on a handful of extreme winning trades.

A desirable initial research target is:

`Maximum equity drawdown <= 12%`

because this aligns with the hard research drawdown guard.

Lower is preferable.

---

## 44. Strategy Failure Is Allowed

If Strategy V1 fails validation:

Report:

`STRATEGY VALIDATION FAILED`

Do not conceal the result.

Do not:

* remove losing trades;
* weaken risk controls;
* use future information;
* endlessly optimize the final test period;
* add martingale;
* add uncontrolled grid recovery.

A failed strategy experiment is valid research information.

---

## 45. Data Collection for Future AI

Every valid historical setup should eventually provide enough information for ML research.

Potential recorded features:

* direction;
* timestamp;
* M15 EMA50;
* M15 EMA200;
* EMA slope;
* ADX;
* M5 EMA20;
* M5 EMA50;
* RSI;
* ATR;
* ATR percentage;
* spread;
* spread/ATR ratio;
* pullback distance;
* candle body;
* candle range;
* session;
* stop distance;
* reward/risk;
* market regime;
* trade outcome.

This dataset will later support ML trade-quality filtering.

---

## 46. ML Must Not Be Added Yet

Strategy V1 must initially run without ML.

The correct development order is:

`Deterministic Strategy`

↓

`Backtest`

↓

`Risk Validation`

↓

`Out-of-Sample Evaluation`

↓

`Dataset`

↓

`ML Filter`

Do not skip directly to AI.

---

## 47. Future ML Role

When introduced, the initial ML model should evaluate Strategy V1 setups.

Conceptually:

`Strategy V1 finds BUY`

↓

`ML evaluates setup quality`

↓

`Probability = 0.72`

↓

`Risk Engine`

↓

`Trade / Reject`

ML should initially filter trades rather than invent unrestricted trades.

---

## 48. Explainability

The system should make it possible to answer:

"Why did the bot enter this trade?"

Example:

`BUY accepted`

* M15 bullish trend
* EMA slope positive
* ADX 27
* pullback within 0.18 ATR
* bullish confirmation
* RSI 58
* spread acceptable
* volatility acceptable
* risk approved

Or:

`BUY rejected`

* daily loss hard limit reached

This observability is mandatory.

---

## 49. Testing Requirements

Automated Strategy V1 tests should cover at least:

### BUY

* valid bullish setup generates BUY candidate;
* missing trend returns WAIT;
* weak ADX returns WAIT;
* invalid pullback returns WAIT;
* bearish confirmation returns WAIT;
* RSI too low returns WAIT;
* RSI too high returns WAIT.

### SELL

* valid bearish setup generates SELL candidate;
* missing trend returns WAIT;
* weak ADX returns WAIT;
* invalid pullback returns WAIT;
* bullish confirmation returns WAIT;
* RSI too high returns WAIT;
* RSI too low returns WAIT.

### Shared

* outside session returns WAIT;
* existing position blocks entry;
* cooldown blocks entry;
* daily trade limit blocks entry;
* invalid market data blocks trading;
* duplicate signal cannot create duplicate entry;
* future data is not required to calculate a decision.

Boundary conditions must be tested.

---

## 50. Strategy Responsibilities

Strategy is responsible for:

* identifying market setup;
* deciding BUY candidate;
* deciding SELL candidate;
* deciding WAIT;
* describing the reason.

Strategy is not responsible for unrestricted:

* lot sizing;
* account risk;
* drawdown override;
* broker credential management;
* database storage;
* live-account activation.

---

## 51. Risk Engine Authority

Final pipeline:

`Strategy Candidate`

↓

`Risk Engine`

↓

`APPROVED or REJECTED`

↓

`Execution`

Even an ideal-looking strategy setup cannot bypass:

* daily loss protection;
* weekly loss protection;
* drawdown protection;
* exposure limits;
* position limits;
* spread protection;
* emergency shutdown.

---

## 52. Initial Strategy Philosophy

Strategy V1 deliberately favors:

* fewer explainable trades;
* controlled risk;
* deterministic behavior;
* simple exits;
* measurable assumptions.

It deliberately avoids:

* high-frequency complexity;
* martingale;
* grid recovery;
* unrestricted averaging;
* opaque AI decisions;
* excessive indicator stacking.

---

## 53. Core Principle

Strategy V1 exists to establish a trustworthy baseline.

The objective is not to make the historical equity curve look impressive.

The objective is to determine whether a simple, controlled, reproducible XAUUSD trading process demonstrates a genuine statistical edge after realistic costs and risk controls.
