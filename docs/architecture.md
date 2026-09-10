# GoldAiTrader — Architecture

## 1. Purpose

GoldAiTrader is a modular C#/.NET automated trading research platform focused initially on XAUUSD.

The architecture must allow trading logic to be developed and tested independently from:

- cTrader;
- broker connectivity;
- PostgreSQL;
- ML.NET;
- external services.

The core principle is:

Trading rules and risk calculations must remain testable without requiring a broker or live trading platform.

---

## 2. Architectural Goals

The architecture should prioritize:

- clear separation of concerns;
- testable domain logic;
- broker independence where practical;
- demo-first execution;
- deterministic behavior;
- explicit risk controls;
- reproducible backtesting;
- replaceable infrastructure;
- maintainable C# code;
- gradual introduction of complexity.

Avoid unnecessary microservices and distributed infrastructure during the initial phases.

GoldAiTrader should begin as a modular .NET solution.

---

## 3. High-Level Flow

The expected trading flow is:

Market Data
→ Market Analysis
→ Market Regime
→ Strategy
→ Signal
→ Optional ML Filter
→ Risk Engine
→ Position Sizing
→ Execution
→ Trade Management
→ Persistence / Logging

The Risk Engine has final authority before execution.

A BUY or SELL signal does not automatically mean a trade must be placed.

Possible final decisions include:

- BUY
- SELL
- WAIT
- REJECT

---

## 4. Initial Solution Structure

The solution may evolve toward:

src/
- GoldAiTrader.Core
- GoldAiTrader.Market
- GoldAiTrader.Risk
- GoldAiTrader.Strategy
- GoldAiTrader.Execution
- GoldAiTrader.Bot
- GoldAiTrader.Data
- GoldAiTrader.ML

tests/
- GoldAiTrader.Core.Tests
- GoldAiTrader.Market.Tests
- GoldAiTrader.Risk.Tests
- GoldAiTrader.Strategy.Tests
- GoldAiTrader.Execution.Tests
- GoldAiTrader.ML.Tests

Not every project must be created immediately.

Projects should be introduced only when architectural separation provides real value.

---

## 5. GoldAiTrader.Core

`GoldAiTrader.Core` contains fundamental domain concepts shared across the system.

It must not depend directly on:

- cTrader;
- Entity Framework Core;
- PostgreSQL;
- ML.NET;
- broker-specific SDKs.

Possible responsibilities include:

- trading direction;
- strategy decisions;
- market snapshots;
- trade requests;
- risk decisions;
- position information;
- trade result models;
- common value objects;
- shared interfaces where appropriate.

Example conceptual domain types:

- TradeDirection
- TradeSignal
- TradeDecision
- MarketSnapshot
- TradeSetup
- TradeRequest
- RiskAssessment
- PositionSize
- TradeResult

Core should remain lightweight.

---

## 6. GoldAiTrader.Market

The Market project is responsible for transforming raw market information into usable trading features.

Responsibilities may include:

- OHLC processing;
- bid/ask handling;
- spread calculations;
- ATR;
- RSI;
- ADX;
- EMA calculations;
- momentum;
- volatility;
- session information;
- price structure;
- market-regime inputs.

It should not place orders.

Conceptually:

Raw Price Data
→ Feature Calculation
→ Market State

The strategy consumes the resulting market state.

---

## 7. GoldAiTrader.Risk

The Risk project is responsible for capital protection.

It contains logic such as:

- risk-per-trade calculation;
- position sizing;
- daily loss limits;
- weekly loss limits;
- drawdown calculations;
- maximum exposure;
- maximum concurrent positions;
- spread validation;
- volatility protection;
- emergency shutdown state;
- trade rejection reasons.

Risk logic must remain independently testable.

The detailed requirements are defined in:

`docs/risk-policy.md`

The Risk Engine has authority to reject any strategy or ML-approved signal.

---

## 8. GoldAiTrader.Strategy

The Strategy project contains deterministic trading logic.

The first strategy must not depend on machine learning.

Initial Strategy V1 should research a combination of:

- trend;
- pullback;
- momentum;
- volatility;
- session;
- price structure;
- market regime.

The strategy produces decisions such as:

- BUY candidate;
- SELL candidate;
- WAIT.

It does not determine unrestricted position size.

Position sizing belongs to the Risk project.

---

## 9. GoldAiTrader.Execution

The Execution project defines how approved trade requests become broker orders.

Responsibilities may include:

- order submission;
- order modification;
- position closing;
- broker response handling;
- duplicate-order protection;
- execution-error handling;
- slippage recording;
- mapping domain quantities to broker-supported quantities.

Execution should depend on abstractions wherever practical.

Broker-specific implementation should not leak unnecessarily into Core or Strategy.

---

## 10. GoldAiTrader.Bot

The Bot project is the composition/integration layer for cTrader.

It may eventually:

- receive cTrader callbacks;
- obtain XAUUSD market information;
- construct market snapshots;
- invoke market analysis;
- invoke strategy;
- invoke the ML filter when enabled;
- invoke the Risk Engine;
- invoke execution;
- record decisions and results.

The cBot should remain relatively thin.

Avoid placing all trading, risk, and ML logic directly inside one cBot class.

Conceptually:

cTrader
↓
GoldAiTrader.Bot
↓
Application/domain components
↓
Execution Adapter
↓
cTrader

---

## 11. cTrader Boundary

cTrader is an external platform dependency.

The architecture should isolate cTrader-specific types.

Where practical:

cTrader type
→ Adapter / Mapper
→ GoldAiTrader domain type

This allows important logic to be tested without launching cTrader.

Examples:

cTrader Symbol
→ SymbolSpecification

cTrader Bars
→ MarketBar collection

cTrader Position
→ PositionSnapshot

---

## 12. GoldAiTrader.Data

The Data project is introduced after core trading and backtesting functionality exists.

Technology:

- PostgreSQL;
- Entity Framework Core.

Responsibilities may include:

- trade persistence;
- market-feature persistence;
- strategy decisions;
- ML predictions;
- model metadata;
- research results.

Entity Framework entities must not automatically become domain models.

Keep persistence concerns separate from trading logic.

---

## 13. Database Direction

Potential entities include:

Trade

- Id
- Symbol
- Direction
- EntryTime
- ExitTime
- EntryPrice
- ExitPrice
- StopLoss
- TakeProfit
- PositionSize
- RiskPercent
- ProfitLoss
- ExitReason

TradeFeature

- TradeId
- ATR
- RSI
- ADX
- EMA values
- Spread
- Volatility
- MarketRegime
- Session

Prediction

- TradeId
- ModelVersion
- Probability
- Decision

The schema may evolve as research requirements become clearer.

---

## 14. GoldAiTrader.ML

Machine learning is introduced only after deterministic strategy infrastructure is operational.

Initial technology:

ML.NET

Initial ML responsibility:

Trade-quality filtering.

Conceptually:

Strategy produces valid setup
↓
ML model evaluates setup
↓
Probability / quality score
↓
Risk Engine
↓
Execution or rejection

ML must not bypass risk controls.

The initial ML model should not have unrestricted authority to generate arbitrary trades.

---

## 15. ML Boundary

The ML project should expose abstractions such as a trade-quality predictor.

Conceptually:

TradeSetup + MarketFeatures
→ TradeQualityPrediction

Possible result:

Probability = 0.73

The strategy and Risk Engine determine how that prediction is used.

Model implementation details must remain isolated from core trading rules.

---

## 16. Backtesting Architecture

Backtesting must reuse as much genuine strategy and risk logic as practical.

Avoid maintaining:

- one strategy implementation for backtests;
- another unrelated implementation for trading.

Conceptually:

Historical Data
↓
Market Engine
↓
Strategy
↓
Risk Engine
↓
Simulated Execution
↓
Performance Metrics

This reduces differences between research and actual trading behavior.

---

## 17. Execution Abstraction

Execution should support at least two environments eventually:

1. simulated/backtest execution;
2. cTrader demo execution.

Conceptually:

ITradeExecutor

Implementations:

- SimulatedTradeExecutor
- CTraderTradeExecutor

Live execution must not be enabled automatically.

---

## 18. Clock / Time Abstraction

Trading logic often depends on:

- sessions;
- daily resets;
- weekly resets;
- candle timestamps;
- cooldown periods.

Avoid unnecessarily coupling critical logic to the machine's current clock.

Where valuable, introduce a testable time abstraction.

This allows automated tests to simulate:

- end of day;
- new trading day;
- weekly reset;
- cooldown completion.

---

## 19. Configuration

Use strongly typed configuration objects.

Potential categories include:

RiskOptions

- RiskPerTradePercent
- DailyLossWarningPercent
- DailyLossLimitPercent
- WeeklyLossLimitPercent
- DrawdownWarningPercent
- HardDrawdownPercent
- MaxOpenPositions
- MaxTotalRiskPercent

StrategyOptions

- indicator periods;
- entry thresholds;
- session settings;
- volatility rules.

ExecutionOptions

- demo/live mode;
- spread limits;
- slippage limits;
- retry policy.

Avoid scattering trading constants through source files.

---

## 20. Dependency Direction

Prefer dependency direction toward stable domain logic.

Conceptually:

Bot
↓
Strategy / Risk / Market / Execution abstractions
↓
Core

Infrastructure dependencies should point inward through abstractions where practical.

Core must not depend on Bot.

Core must not depend on cTrader.

Core must not depend on PostgreSQL.

Core must not depend on ML.NET.

---

## 21. Testing Architecture

High-value logic must be testable independently.

Priority areas:

- position sizing;
- risk calculations;
- drawdown;
- daily/weekly loss controls;
- signal rules;
- spread validation;
- trade rejection;
- market feature calculations;
- duplicate execution protection;
- backtesting calculations.

Tests should not require a live broker.

---

## 22. Logging

Logging should capture important decisions rather than only exceptions.

Examples:

Signal generated
→ BUY candidate

Risk evaluation
→ rejected because daily loss threshold reached

Execution
→ order accepted/rejected

ML
→ confidence 0.71

Drawdown
→ defensive mode activated

Do not log secrets.

---

## 23. Error Handling

Fail safely.

If required information is invalid or uncertain:

DO NOT TRADE

Execution failures must not result in infinite retries.

Unknown states should default toward capital protection rather than aggressive execution.

---

## 24. Demo-First Integration

The initial cTrader integration must operate in demo-safe mode.

The architecture must make accidental live trading difficult.

Live trading must require explicit deliberate configuration and must not be part of normal development or test workflows.

---

## 25. Build Evolution

Recommended implementation sequence:

Phase 1
Core domain foundation

Phase 2
Market abstractions/features

Phase 3
Risk Engine

Phase 4
Deterministic strategy

Phase 5
Execution abstraction and cTrader demo adapter

Phase 6
Backtesting and metrics

Phase 7
Robustness/out-of-sample testing

Phase 8
PostgreSQL persistence

Phase 9
ML.NET trade-quality filtering

Phase 10
Walk-forward evaluation

Phase 11
Demo deployment readiness

Phase 12
Final technical report

---

## 26. Avoid Premature Complexity

Do not introduce unless justified:

- Kubernetes;
- message brokers;
- distributed microservices;
- multiple databases;
- event-sourcing frameworks;
- unnecessary cloud infrastructure;
- excessive abstraction layers.

This project should become complex only where the trading/research problem requires it.

---

## 27. Future Extensibility

Although the initial market is XAUUSD, good abstractions should avoid unnecessarily hardcoding Gold into every domain object.

However, do not sacrifice clarity for hypothetical future multi-asset support.

Build for XAUUSD first.

Generalize later when actual requirements justify it.

---

## 28. Architectural Decision Rule

When deciding where code belongs, ask:

"Could this logic be tested without cTrader?"

If yes, it probably should not live directly inside the cBot.

Examples:

Position sizing
→ Risk project

EMA relationship
→ Market/Strategy

Daily loss calculation
→ Risk

Order submission
→ Execution/cTrader adapter

Database storage
→ Data

ML prediction
→ ML

cTrader lifecycle callbacks
→ Bot

---

## 29. Definition of Healthy Architecture

A healthy GoldAiTrader architecture should allow us to:

- unit-test risk without cTrader;
- unit-test strategy without PostgreSQL;
- backtest without live broker access;
- replace simulated execution with cTrader execution;
- add ML without rewriting the strategy engine;
- add persistence without changing fundamental risk calculations;
- inspect why a trade was accepted or rejected.

---

## 30. Core Principle

The architecture exists to support trustworthy trading research.

Correctness, observability, testability, and capital protection are more important than minimizing the number of source files or maximizing implementation speed.
