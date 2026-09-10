# GoldAiTrader — Machine Learning Specification

## 1. Purpose

This document defines how machine learning may be introduced into GoldAiTrader.

Machine learning is not the primary trading system.

The deterministic Strategy V1 and Risk Engine must work independently before ML is introduced.

The initial purpose of ML is:

**trade-quality filtering**

Conceptually:

`Deterministic Strategy`

↓

`Valid BUY / SELL candidate`

↓

`ML Quality Model`

↓

`Probability / Quality Score`

↓

`Risk Engine`

↓

`APPROVE / REJECT`

↓

`Execution`

ML must never bypass the Risk Engine.

---

## 2. Initial Technology

Initial machine-learning technology:

`ML.NET`

C# remains the primary development language.

Python must not be introduced merely because the project contains AI.

Python or another ML stack may be considered later only if:

* ML.NET becomes materially limiting;
* a specific model provides justified research value;
* the additional operational complexity is worthwhile.

---

## 3. ML Is Introduced Later

Do not implement the ML phase until:

* deterministic Strategy V1 exists;
* Risk Engine exists;
* position sizing is tested;
* backtesting works;
* realistic transaction costs are represented;
* strategy decisions can be recorded;
* historical features can be generated chronologically;
* sufficient setup data exists.

Correct order:

`Strategy`

↓

`Risk`

↓

`Backtesting`

↓

`Historical Dataset`

↓

`ML`

---

## 4. Initial ML Responsibility

The first ML model should estimate the quality of an already-valid deterministic setup.

Example question:

> Given the information available when this Strategy V1 setup occurred, what is the estimated probability that the trade reaches its intended objective before its defined adverse outcome?

The model initially acts as a filter.

It does not freely decide when to trade from raw price data.

---

## 5. ML Must Not Control Risk

ML must never independently:

* choose unrestricted position size;
* increase configured risk;
* remove stop losses;
* bypass daily loss limits;
* bypass weekly loss limits;
* bypass drawdown limits;
* open additional recovery positions;
* implement martingale;
* activate live trading;
* override emergency shutdown.

Risk authority remains:

`GoldAiTrader.Risk`

---

## 6. Initial Prediction Type

Initial ML problem:

`Binary classification`

Possible conceptual labels:

* PositiveSetup
* NegativeSetup

The precise label must be defined consistently before model training begins.

Probability output should be available where the selected ML.NET trainer supports it.

---

## 7. Label Definition

The label must be based on a clearly defined trade outcome.

For Strategy V1, an initial research label may be:

`PositiveSetup = true`

when the valid historical setup reaches its configured profit objective before reaching its initial stop-loss condition.

Otherwise:

`PositiveSetup = false`

However, labels must be generated only for training after the historical trade has completed.

Future outcome information must never become an input feature.

---

## 8. Label Leakage Prevention

Trade outcome may be used as the target label.

It must not appear directly or indirectly in model input features.

Forbidden input examples include:

* future maximum favorable excursion;
* future maximum adverse excursion;
* exit price;
* exit reason;
* future high;
* future low;
* eventual profit/loss;
* number of bars until target;
* future spread;
* future market regime.

If information did not exist when the trade decision was made:

**it cannot be an input feature.**

---

## 9. Initial Feature Candidates

Potential features available at signal time include:

* trade direction;
* hour of day;
* trading session;
* M15 EMA50;
* M15 EMA200;
* EMA50/EMA200 relationship;
* M15 EMA50 slope;
* ADX;
* M5 EMA20;
* M5 EMA50;
* EMA20/EMA50 relationship;
* RSI;
* ATR;
* ATR as percentage of price;
* current spread;
* spread/ATR ratio;
* pullback distance from EMA20;
* pullback distance normalized by ATR;
* confirmation candle body size;
* confirmation candle range;
* candle body/range ratio;
* distance from recent swing high;
* distance from recent swing low;
* planned stop distance;
* planned reward/risk ratio;
* recent return values;
* current strategy regime.

All features must be computable using data available at signal time.

---

## 10. Feature Simplicity

Do not begin with hundreds or thousands of features.

Start with a compact, explainable feature set based on Strategy V1.

Additional features should require a research reason.

Avoid adding technical indicators merely to increase feature count.

---

## 11. Raw Price Features

Raw prices may be included only when there is a clear reason.

Normalized relationships are often more useful across different XAUUSD price levels.

Examples:

Prefer investigating:

`ATR / Price`

rather than ATR alone.

Prefer:

`DistanceFromEma / ATR`

rather than only raw dollar distance.

This is a research preference, not an absolute restriction.

---

## 12. Feature Engineering

Feature engineering must be deterministic.

Given identical market data and configuration, generated features must be reproducible.

Feature generation should be shared between:

* historical dataset creation;
* backtesting;
* demo prediction.

Do not maintain incompatible training and production feature calculations.

---

## 13. Feature Schema Versioning

ML input schemas must be versioned.

Example:

`TradeFeaturesV1`

If features materially change:

`TradeFeaturesV2`

Do not silently change the meaning or ordering of features while reusing an old model.

---

## 14. Strategy Version Relationship

Every ML training record must identify the deterministic strategy version that created the setup.

Initial strategy:

`TrendPullbackV1`

A model trained on Strategy V1 setups must not automatically be assumed compatible with Strategy V2.

Record:

* StrategyVersion
* FeatureSchemaVersion
* ModelVersion

---

## 15. Historical Dataset

The ML dataset should originate from chronologically correct Strategy V1 research.

Each row conceptually represents:

`one valid deterministic setup`

Possible fields:

* SetupId
* SignalTime
* StrategyVersion
* Direction
* Features
* Label
* OutcomeMetadata

Outcome metadata may be retained for analysis but must not enter the input feature vector when it contains future information.

---

## 16. Rejected Strategy Setups

Do not automatically train the initial model on conditions where Strategy V1 itself produced `WAIT`.

The initial ML research target is:

**quality of deterministic strategy setups**

not:

**predict every XAUUSD candle**

A later research phase may investigate broader opportunities separately.

---

## 17. Chronological Splits

Time-series data must be split chronologically.

Do not randomly shuffle the entire dataset before train/test separation.

Use:

* training;
* validation;
* final out-of-sample.

Conceptually:

Earlier period:

`TRAIN`

↓

Later period:

`VALIDATION`

↓

Newest untouched period:

`FINAL OUT-OF-SAMPLE`

---

## 18. Final Out-of-Sample Protection

The final out-of-sample dataset must not be repeatedly used to choose:

* features;
* hyperparameters;
* model type;
* prediction threshold.

If those decisions are repeatedly changed after seeing final results, the period is no longer genuinely unseen.

---

## 19. Walk-Forward ML Evaluation

After the basic model works, support walk-forward evaluation.

Conceptually:

Window 1:

Train on past

→ Test next period

Window 2:

Move forward

→ Train using data available up to that point

→ Test next unseen period

Repeat.

This is preferred over relying on a single historical split.

---

## 20. Initial Model Candidates

Initial ML.NET research may compare a small number of appropriate tabular classification algorithms.

Candidates may include:

* logistic regression;
* FastTree;
* FastForest;
* LightGBM if available and appropriate in the selected ML.NET environment.

Do not compare dozens of algorithms merely to find one lucky result.

Start with a simple interpretable baseline.

---

## 21. Baseline Model

The first model should be simple.

A logistic-regression-type baseline is useful because it provides a reference point.

More complex models must demonstrate improvement on unseen data.

Complexity without out-of-sample improvement is not progress.

---

## 22. Model Evaluation Metrics

Classification evaluation should include where appropriate:

* ROC AUC;
* PR AUC;
* accuracy;
* precision;
* recall;
* F1 score;
* log loss;
* confusion matrix;
* calibration;
* probability distribution.

Because trade outcomes may be imbalanced, accuracy alone is insufficient.

---

## 23. Trading Metrics Still Matter More

A classification model with attractive ML metrics may still make the trading system worse.

Therefore evaluate the model inside the trading pipeline.

Compare:

`Strategy V1 without ML`

versus:

`Strategy V1 + ML Filter`

Trading comparison must include:

* net return;
* maximum equity drawdown;
* profit factor;
* expectancy;
* trade count;
* win rate;
* average R;
* consecutive losses;
* transaction costs.

---

## 24. ML Success Definition

The ML layer is useful only if it improves meaningful out-of-sample trading behavior.

Possible benefits include:

* improved expectancy;
* improved profit factor;
* lower drawdown;
* fewer low-quality trades;
* improved risk-adjusted return.

Higher prediction accuracy alone does not establish usefulness.

---

## 25. Prediction Threshold

The model should produce a probability or score where possible.

Example:

`Probability = 0.71`

The system may use a configurable threshold such as:

`MinimumProbabilityToTrade`

This threshold must be selected using development/validation data.

It must not be repeatedly tuned against locked final out-of-sample data.

---

## 26. Threshold Does Not Replace Risk

Example:

ML probability:

`91%`

But:

Daily hard loss limit reached.

Final result:

`TRADE REJECTED`

Another example:

ML probability:

`85%`

But:

Spread unacceptable.

Final result:

`TRADE REJECTED`

Risk and execution safety remain authoritative.

---

## 27. Low Confidence Behavior

When a strategy setup is valid but ML confidence is below the configured threshold:

`REJECT / WAIT`

Do not automatically reverse the trade.

A low-confidence BUY is not automatically a SELL.

---

## 28. Missing Model Behavior

The system must define safe behavior when the ML model is:

* missing;
* corrupt;
* incompatible;
* unavailable;
* wrong schema version.

Initial safe default:

Run deterministic strategy only if explicitly configured to allow deterministic fallback.

Otherwise:

`DO NOT PLACE ML-DEPENDENT TRADE`

Never guess predictions.

---

## 29. ML Optionality

The system must support:

`ML Enabled`

and:

`ML Disabled`

This allows direct comparison between:

* baseline deterministic strategy;
* ML-filtered strategy.

The deterministic system must remain functional without ML.

---

## 30. Shadow Mode

Before ML is allowed to affect demo trades, support a shadow evaluation mode.

In shadow mode:

* Strategy V1 trades normally according to approved deterministic rules;
* ML generates predictions;
* ML prediction is logged;
* ML does not alter execution.

This provides forward evidence of ML behavior without allowing it to control trades.

---

## 31. Demo ML Mode

After successful shadow evaluation, the ML filter may be enabled on a demo account.

The system should record:

* setups accepted by ML;
* setups rejected by ML;
* hypothetical outcome of rejected setups where research infrastructure permits;
* actual traded outcomes.

This helps evaluate whether the filter adds value.

---

## 32. No Autonomous Live ML Deployment

Contributors and automated workflows must never:

* enable an ML model on a live account;
* switch trading mode to live;
* supply real broker credentials;
* remove the demo-first requirement.

Live validation requires explicit user action after independent review.

---

## 33. Model Version

Every trained model requires a unique version or identifier.

Possible format:

`TrendPullbackV1-TradeQuality-2026-001`

Record:

* model version;
* strategy version;
* feature schema version;
* trainer;
* hyperparameters;
* training period;
* validation period;
* training timestamp;
* source commit where practical.

---

## 34. Model Metadata

Persist or serialize model metadata separately from or alongside the model artifact.

Metadata should include:

* ModelVersion
* FeatureSchemaVersion
* StrategyVersion
* TrainingStart
* TrainingEnd
* ValidationStart
* ValidationEnd
* Trainer
* Hyperparameters
* Metrics
* ProbabilityThreshold
* CreatedAt

---

## 35. Model Artifact Storage

Do not commit large generated model binaries automatically unless there is a deliberate repository policy.

Model artifacts may later be stored using an appropriate versioned artifact mechanism.

Do not store credentials with models.

---

## 36. Reproducibility

Where algorithms involve randomness, configure and record random seeds where supported.

Given the same:

* dataset;
* code;
* configuration;
* seed;
* ML library version;

training should be as reproducible as practical.

---

## 37. Class Imbalance

Inspect label distribution.

Example:

`PositiveSetup: 28%`

`NegativeSetup: 72%`

Do not ignore imbalance.

Evaluate appropriate:

* precision;
* recall;
* PR AUC;
* class weighting or other techniques if justified.

Do not blindly oversample time-series data in a way that creates leakage.

---

## 38. Calibration

Because the system may use predicted probability as a threshold, probability calibration matters.

Compare whether:

Predicted probability around 70%

actually corresponds reasonably to observed success frequency in unseen data.

Poorly calibrated probabilities must not be presented as literal confidence.

---

## 39. Confidence Terminology

Avoid misleading statements such as:

`AI is 90% certain Gold will rise`

unless the probability has a precise model-defined meaning.

Preferred language:

`Model-estimated probability for this Strategy V1 setup: 0.73`

The probability refers to the defined classification label, not certainty about the market.

---

## 40. Feature Importance

Where supported and scientifically reasonable, collect feature-importance diagnostics.

Potential purpose:

* detect useless features;
* understand model behavior;
* identify suspicious leakage;
* improve explainability.

Feature importance does not prove causation.

---

## 41. Explainability

For every ML-filtered decision, record enough information to answer:

* which model was used?
* what probability was produced?
* what threshold was required?
* was the trade accepted or rejected?
* what were the major strategy conditions?
* what did the Risk Engine decide?

Example:

`Strategy: BUY`

`Model: TrendPullbackV1-TradeQuality-001`

`Probability: 0.74`

`Required: 0.65`

`ML: ACCEPT`

`Risk: APPROVE`

`Final: BUY`

---

## 42. Data Leakage Tests

Automated tests should specifically guard against leakage.

Examples:

* features cannot access post-signal bars;
* labels are generated after outcomes;
* training pipeline does not include outcome columns as features;
* chronological split boundaries are respected;
* validation records are not used during model fit;
* final out-of-sample records are not used during model selection.

---

## 43. Feature Generation Tests

Unit tests should verify important feature calculations.

Examples:

* ATR normalization;
* EMA distance;
* spread/ATR;
* candle body ratio;
* session encoding;
* direction encoding;
* pullback distance;
* no future-bar access.

---

## 44. Model Integration Tests

Tests should cover:

* compatible model loads successfully;
* incompatible schema is rejected;
* missing model handled safely;
* prediction returns expected structure;
* threshold handling works;
* low probability rejects trade;
* high probability still cannot bypass Risk Engine;
* ML-disabled mode works;
* shadow mode does not alter execution.

---

## 45. Model Training Separation

Keep training concerns separate from real-time inference.

Conceptually:

`GoldAiTrader.ML.Training`

may handle:

* loading research dataset;
* preprocessing;
* training;
* evaluation;
* artifact creation.

Real-time component:

`TradeQualityPredictor`

should focus on loading the approved model and producing predictions.

---

## 46. Persistence

When PostgreSQL is introduced, useful ML records may include:

### Predictions

* PredictionId
* SetupId
* ModelVersion
* Probability
* Threshold
* MLDecision
* CreatedAt

### ModelMetadata

* ModelVersion
* StrategyVersion
* FeatureSchemaVersion
* TrainingRange
* ValidationRange
* Trainer
* Metrics
* CreatedAt

Schema may evolve.

---

## 47. Research Dataset Version

Datasets should have an identifiable version.

Example:

`TrendPullbackV1-Dataset-001`

A training result should identify the exact dataset version used.

This avoids comparing models trained on silently different datasets.

---

## 48. Data Quality Report

Before training, generate basic dataset diagnostics:

* number of setups;
* positive labels;
* negative labels;
* missing values;
* date range;
* BUY/SELL distribution;
* session distribution;
* major feature ranges.

Training should fail or warn appropriately when essential data quality is unacceptable.

---

## 49. Missing Features

Do not silently replace critical missing market features with arbitrary values.

Choose an explicit policy:

* reject record;
* use scientifically justified imputation;
* mark missing where model supports it.

Document the approach.

For real-time prediction, unsafe missing data should generally result in:

`NO ML-APPROVED TRADE`

---

## 50. Hyperparameter Search

Hyperparameter tuning must be controlled.

Avoid enormous brute-force search spaces.

Every increase in search flexibility increases overfitting risk.

Prefer:

* limited parameter ranges;
* chronological validation;
* stable behavior;
* reproducible experiments.

---

## 51. Experiment Tracking

Every serious ML experiment should record:

* experiment ID;
* Git commit;
* dataset version;
* feature schema;
* strategy version;
* model type;
* hyperparameters;
* train range;
* validation range;
* metrics;
* probability threshold;
* trading simulation results.

This may initially be represented with JSON/Markdown before introducing a more sophisticated tracking system.

---

## 52. Baseline Comparison

ML must be compared against:

`Strategy V1 without ML`

At minimum report:

| Metric             | Baseline | ML Filter |
| ------------------ | -------: | --------: |
| Trades             |          |           |
| Net Return         |          |           |
| Max Equity DD      |          |           |
| Profit Factor      |          |           |
| Expectancy         |          |           |
| Win Rate           |          |           |
| Avg R              |          |           |
| Consecutive Losses |          |           |

If ML does not materially improve the system:

Do not use it merely because it is AI.

---

## 53. Reduced Trade Count

An ML filter will likely reduce trade count.

That is acceptable if the remaining trades demonstrate improved quality.

However, a model that reduces hundreds of trades to a tiny handful may provide insufficient statistical evidence.

Always report sample size.

---

## 54. BUY / SELL Evaluation

Evaluate ML performance separately for:

* BUY setups;
* SELL setups.

If one direction performs materially differently, investigate.

Do not assume one shared threshold is automatically optimal or justified.

Any direction-specific policy must be validated without overfitting.

---

## 55. Regime Evaluation

Where sample size permits, analyze ML performance by:

* trend strength;
* volatility;
* session;
* year;
* market regime.

A model that only works in one narrow historical environment should be treated cautiously.

---

## 56. Drift

Market behavior may change over time.

The architecture should allow monitoring for model degradation.

Potential future drift indicators:

* prediction distribution changes;
* hit-rate changes;
* expectancy decline;
* calibration deterioration;
* feature distribution shifts.

Automatic retraining must not be introduced without explicit validation rules.

---

## 57. Retraining

Initial model retraining should be deliberate, not continuous.

A retrained model must undergo:

* validation;
* walk-forward testing;
* comparison against current approved model;
* risk review.

A newer model is not automatically better.

---

## 58. Champion / Challenger

Future architecture may support:

`Champion model`

currently approved

versus:

`Challenger model`

being evaluated.

The challenger should not automatically replace the champion.

Replacement requires predefined validation criteria.

---

## 59. Failed ML Experiment

If ML degrades the deterministic strategy:

Report:

`ML VALIDATION FAILED`

Do not hide the result.

Possible final system state may legitimately be:

`Strategy V1 validated`

`ML filter rejected`

The project does not require AI to be useful merely because AI was planned.

---

## 60. ML and Drawdown

Particular attention should be given to whether ML reduces:

* poor entries;
* consecutive losses;
* maximum equity drawdown.

A model that slightly increases profit while dramatically increasing drawdown may be undesirable.

Capital preservation remains the higher priority.

---

## 61. Transaction Costs

ML-filtered backtests must use the same realistic transaction-cost assumptions as the deterministic baseline.

Do not compare:

baseline with costs

against:

ML without costs.

Comparisons must be fair.

---

## 62. Final ML Out-of-Sample Evaluation

The final ML evaluation must use data that was not used for:

* feature selection;
* model choice;
* hyperparameter tuning;
* threshold selection.

Report final results once as an independent evaluation wherever practical.

---

## 63. ML Completion Criteria

The ML engineering phase is complete when:

* dataset generation works;
* features are chronological;
* labels are defined;
* leakage protections exist;
* training pipeline works;
* baseline model works;
* model evaluation works;
* artifacts can be versioned;
* inference works;
* ML-disabled mode works;
* shadow mode works;
* integration with Strategy/Risk works;
* automated tests pass;
* documentation exists.

This does not mean the ML model has been validated as beneficial.

---

## 64. ML Validation Criteria

ML may be considered useful only when independent evaluation provides reasonable evidence that it improves the trading process.

Consider:

* expectancy;
* drawdown;
* profit factor;
* sample size;
* cost robustness;
* walk-forward consistency;
* calibration;
* regime stability.

No single metric determines acceptance.

---

## 65. Production Safety

Inference failures must fail safely.

Model exceptions must not result in:

* uncontrolled order execution;
* random trade decisions;
* bypassing strategy;
* bypassing risk.

Default uncertain behavior:

`DO NOT TRADE`

or deterministic fallback only when explicitly configured.

---

## 66. Logging

Log ML decisions including:

* timestamp;
* setup ID;
* strategy version;
* model version;
* feature schema version;
* predicted probability;
* configured threshold;
* ML decision;
* Risk Engine decision;
* final trading decision.

Do not log credentials or sensitive configuration.

---

## 67. Performance

Inference should be fast enough for the M5 Strategy V1 use case.

There is no need for ultra-low-latency infrastructure during initial development.

Correctness and reproducibility matter more than microsecond optimization.

---

## 68. No LLM Trading Decisions

Large language models are not part of Strategy V1 execution.

Do not send every XAUUSD candle to a chatbot to ask:

`BUY or SELL?`

LLMs may eventually assist with offline research or textual analysis, but they must not replace deterministic execution and tested risk controls without a separate approved specification.

---

## 69. No Autonomous Internet Sentiment System Yet

Do not introduce:

* Twitter/X sentiment;
* Reddit sentiment;
* news scraping;
* economic-news NLP;
* social-media signals

during the initial ML phase.

These would introduce additional data quality, timestamp, licensing, and leakage risks.

They may be researched later as separate experiments.

---

## 70. Future Deep Learning

Do not introduce neural networks merely for sophistication.

Deep learning may be researched later only if:

* the baseline system is mature;
* sufficient data exists;
* simpler models have been properly evaluated;
* there is a specific hypothesis;
* testing infrastructure can detect overfitting.

---

## 71. Repository Architecture

Potential ML-related code may eventually include concepts such as:

`GoldAiTrader.ML`

* FeatureVector
* FeatureGenerator
* TradeOutcomeLabel
* TradeQualityPrediction
* ITradeQualityPredictor
* MlNetTradeQualityPredictor
* ModelMetadata
* ModelLoader

Training concerns may be kept separately from runtime inference when useful.

Exact structure may evolve.

---

## 72. Core Dependency Rule

`GoldAiTrader.Core`

must not depend directly on ML.NET.

ML.NET is an infrastructure/model implementation detail.

Core may define neutral domain contracts that the ML project implements.

---

## 73. Risk Dependency Rule

Risk must not depend on ML approval to function.

The Risk Engine must remain fully functional when ML is:

* disabled;
* missing;
* rejected;
* unavailable.

---

## 74. Final Report

`docs/final-report.md`

must eventually include a dedicated ML section containing:

* dataset;
* strategy version;
* feature schema;
* models evaluated;
* training range;
* validation range;
* out-of-sample range;
* ML metrics;
* deterministic trading metrics;
* ML-filtered trading metrics;
* drawdown comparison;
* limitations;
* final status.

Final status should clearly state one of:

`ML VALIDATED FOR DEMO RESEARCH`

or:

`ML VALIDATION FAILED`

or:

`INSUFFICIENT EVIDENCE`

Never state guaranteed profitability.

---

## 75. Core Principle

Machine learning must earn its place in GoldAiTrader.

If the deterministic strategy performs better without ML, the correct engineering decision is to leave ML disabled.

AI is a tool for improving tested trading decisions, not a substitute for sound strategy design, risk management, and independent validation.
