# GoldAiTrader — Deployment and Operational Safety

## 1. Purpose

This document defines deployment, configuration, operational safety, monitoring, and environment rules for GoldAiTrader.

The project must remain:

**DEMO-FIRST**

through autonomous development and initial validation.

Software completion does not authorize live trading.

---

## 2. Deployment Principle

GoldAiTrader must distinguish clearly between:

* Development
* Backtest
* Demo
* Live

These environments must never be treated as interchangeable.

The default environment must always be safe.

---

## 3. Default Trading Mode

Default:

`Demo`

or:

`Trading Disabled`

Starting the application without explicit configuration must never place a live-money trade.

If the configured environment cannot be determined safely:

`DO NOT TRADE`

---

## 4. Trading Modes

The system should eventually support strongly typed operating modes such as:

* Backtest
* Shadow
* Demo
* Live

### Backtest

Uses historical data and simulated execution.

No broker orders.

### Shadow

Observes current market conditions and generates decisions.

No broker orders.

Useful for:

* strategy observation;
* ML prediction validation;
* risk-system observation.

### Demo

May execute orders only against an explicitly verified demo account.

### Live

Future capability only.

Must not be enabled during normal development or test workflows.

---

## 5. Live Trading Prohibition During Autonomous Development

Contributors and automated workflows must never:

* enable Live mode;
* provide broker credentials;
* modify configuration to enable real-money trading;
* remove demo-account validation;
* bypass environment checks;
* deploy directly to a live trading account;
* execute real-money orders.

Live deployment requires explicit user approval after independent review.

---

## 6. Environment Verification

Before broker-connected trading begins, the system must verify the environment.

Where broker/platform APIs expose account information, validate:

* account identifier;
* account type;
* demo/live state where available;
* symbol availability;
* account currency;
* current equity;
* trading permission.

If demo status cannot be verified:

`BLOCK AUTOMATED EXECUTION`

unless an explicitly approved future live configuration exists.

---

## 7. Instrument Verification

Initial supported instrument:

`XAUUSD`

Before trading:

* verify symbol exists;
* obtain broker symbol specification;
* verify tick size;
* verify tick value;
* verify minimum volume;
* verify maximum volume;
* verify volume step;
* verify market/trading availability.

Never assume every broker uses identical XAUUSD specifications.

---

## 8. Timeframe Verification

Strategy V1 requires:

* M5 execution timeframe;
* M15 context timeframe.

The bot must verify required market data is available.

If required timeframes cannot be produced reliably:

`DO NOT TRADE`

---

## 9. Startup Safety Checks

Before enabling order execution, validate:

* configuration loaded;
* environment known;
* demo status confirmed where required;
* symbol valid;
* market data available;
* required indicator history available;
* Risk Engine operational;
* risk configuration valid;
* execution adapter operational;
* no emergency shutdown active;
* no impossible account values;
* duplicate bot instance protection where applicable.

Failed startup validation:

`TRADING DISABLED`

---

## 10. Configuration Validation

Configuration must be validated before trading.

Examples of invalid configuration:

* RiskPerTradePercent <= 0
* RiskPerTradePercent excessively high
* DailyLossLimit <= 0
* HardDrawdownPercent <= 0
* HardDrawdownPercent less than warning threshold
* MaxOpenPositions < 1
* negative spread limits
* invalid trading-session times
* invalid reward/risk values
* zero stop multiplier

The application should fail safely with clear errors.

---

## 11. Strongly Typed Configuration

Prefer strongly typed settings such as:

### TradingEnvironmentOptions

* Mode
* Symbol
* ExecutionTimeframe
* ContextTimeframe

### RiskOptions

* RiskPerTradePercent
* DailyLossWarningPercent
* DailyLossLimitPercent
* WeeklyLossLimitPercent
* DrawdownWarningPercent
* HardDrawdownPercent
* MaxOpenPositions
* MaxTotalRiskPercent

### StrategyOptions

Defined by:

`docs/strategy.md`

### ExecutionOptions

* MaximumSpread
* MaximumSlippage
* RetryCount
* DemoOnly

---

## 12. Configuration Sources

Use standard .NET configuration where practical.

Potential sources:

* appsettings.json
* appsettings.Development.json
* environment variables
* user secrets for local development
* secure deployment secrets

Never store actual credentials in committed configuration.

---

## 13. Secrets

The following must never be committed:

* broker passwords;
* API tokens;
* database passwords;
* private connection strings containing secrets;
* cloud credentials;
* private certificates;
* refresh tokens.

Provide safe placeholders instead.

Example:

`Database__Password=YOUR_PASSWORD`

not:

an actual password.

---

## 14. Development Secrets

For local development, use appropriate secure mechanisms such as:

* .NET user secrets;
* environment variables;
* locally ignored configuration.

Do not instruct the user to paste secrets into source files.

---

## 15. PostgreSQL Deployment

When PostgreSQL is introduced:

* database configuration must be externalized;
* credentials must not be committed;
* EF Core migrations must be version controlled;
* migrations must be reviewable;
* destructive schema changes require caution;
* application startup should not silently destroy databases.

Production-like database operations must not use:

`EnsureDeleted()`

or equivalent destructive development behavior.

---

## 16. Database Migration Policy

Use EF Core migrations.

Typical workflow:

* create migration;
* inspect migration;
* apply to development database;
* run tests;
* commit migration.

Do not automatically drop/recreate persistent databases during normal application startup.

---

## 17. Logging

Deployment logging should include:

* application startup;
* application version;
* operating mode;
* strategy version;
* model version where enabled;
* symbol;
* trading enabled/disabled;
* environment verification;
* startup validation results;
* Risk Engine state;
* order requests;
* broker responses;
* shutdown events;
* unhandled exceptions.

Never log secrets.

---

## 18. Structured Logging

Prefer structured logging where useful.

Example conceptual event:

`TradeRejected`

Fields:

* Timestamp
* Symbol
* Direction
* StrategyVersion
* Reason
* Equity
* DrawdownPercent
* DailyLossPercent

This makes later investigation easier.

---

## 19. Correlation Identifiers

Where useful, create identifiers for:

* strategy setup;
* trade request;
* broker order;
* position;
* backtest run;
* ML prediction.

This should allow a single trade lifecycle to be traced through logs.

---

## 20. Operational Health

The system should expose enough information to determine:

* application running;
* market data flowing;
* last received market update;
* trading mode;
* Risk Engine state;
* emergency shutdown state;
* database connectivity where relevant;
* model availability where ML is enabled;
* broker connection state.

---

## 21. Stale Market Data Protection

Track the last valid market-data timestamp.

If data becomes unexpectedly stale:

`NO NEW TRADES`

Do not continue making decisions from old prices.

The acceptable stale interval must be configurable based on execution mode/timeframe.

---

## 22. Connection Loss

If broker/platform connectivity is lost:

* stop submitting new orders;
* record connection state;
* avoid infinite retry loops;
* resume only after safe reconnection checks.

Existing broker-side protective orders must not be intentionally removed because of local connection loss.

---

## 23. Retry Policy

Retries must be bounded.

Do not use:

`while(true) submit order`

Execution retry behavior should consider:

* error category;
* market state;
* duplicate-execution risk;
* elapsed time.

Some failures should not be retried automatically.

---

## 24. Duplicate Instance Protection

Running two copies of the same bot against the same account/symbol may cause duplicate trades.

The architecture should support detection or prevention where practical.

Possible identity:

* Account
* Symbol
* StrategyVersion

If duplicate active execution cannot be safely ruled out:

`BLOCK NEW EXECUTION`

---

## 25. Duplicate Order Protection

Every strategy setup should have an identifiable signal identity.

Submitting the same signal repeatedly must not create unintended duplicate positions.

Execution should be idempotent where practical.

---

## 26. Emergency Shutdown

The system must support a trading shutdown state.

Shutdown may be triggered by:

* daily hard loss limit;
* weekly hard loss limit;
* hard drawdown;
* repeated execution failures;
* corrupted configuration;
* invalid account state;
* stale market data;
* explicit administrative action.

During shutdown:

`NO NEW TRADES`

---

## 27. Shutdown Persistence

Where appropriate, severe shutdown state should survive an application restart.

Example:

If the account reached the hard daily loss limit, simply restarting the application must not automatically allow trading again during the same trading day.

The system should reconstruct or persist enough risk state to avoid restart-based bypass.

---

## 28. Graceful Application Shutdown

On normal shutdown:

* stop accepting new trading decisions;
* finish safe in-process persistence;
* flush logs;
* release resources;
* preserve required state.

Do not submit new trades while the application is shutting down.

---

## 29. Existing Positions During Shutdown

Stopping the application must not automatically mean:

`close everything immediately`

unless the defined risk/emergency policy requires it.

Position management behavior during shutdown must be explicit.

Broker-side stop loss and take profit should remain intact where supported.

---

## 30. Application Restart

After restart, reconcile current broker state.

Determine:

* existing positions;
* bot-owned positions;
* current account equity;
* current daily/weekly risk state;
* active shutdown conditions.

Do not assume a clean account simply because the process restarted.

---

## 31. Position Ownership

GoldAiTrader must avoid accidentally managing unrelated manual or external-EA positions.

Where supported, use identifiable metadata such as:

* label;
* comment;
* strategy ID;
* magic/instance identifier;
* account/symbol relationship.

Only manage positions the system is authorized to manage.

---

## 32. Manual Intervention

Manual user changes may occur during demo testing.

Examples:

* manually closing position;
* manually modifying stop;
* account balance adjustment;
* disabling trading.

GoldAiTrader should detect inconsistencies rather than blindly assuming its previous state remains true.

Unexpected external changes should be logged.

---

## 33. Demo Forward Testing

After historical validation, deploy to demo.

Forward-testing objectives:

* execution correctness;
* spread behavior;
* actual slippage;
* broker volume handling;
* cTrader integration stability;
* position reconciliation;
* logging quality;
* risk enforcement.

Demo testing is not merely about profit.

---

## 34. Demo Forward-Test Duration

Do not declare strategy robustness after:

* one day;
* one profitable week;
* a few trades.

The forward-test period must collect a meaningful sample across different market conditions.

Final duration depends on trade frequency and evidence collected.

---

## 35. Forward-Test Comparison

Compare:

`Backtest expectation`

against:

`Demo forward behavior`

Include:

* trade frequency;
* spread;
* slippage;
* win rate;
* expectancy;
* drawdown;
* execution failures;
* rejected orders.

Large divergence requires investigation.

---

## 36. Shadow Deployment

Before demo order execution, the bot may run in:

`Shadow`

mode.

Shadow mode:

* receives live/demo market data;
* creates strategy decisions;
* evaluates Risk Engine;
* logs hypothetical trades;
* submits no broker orders.

This is especially useful for validating platform integration safely.

---

## 37. ML Shadow Deployment

ML should first operate in shadow mode.

Record:

* deterministic strategy decision;
* ML prediction;
* ML threshold result;
* Risk Engine decision;
* hypothetical final action.

No ML-controlled broker trade is required during initial validation.

---

## 38. Demo ML Deployment

After shadow validation:

ML filtering may be enabled on the explicitly verified demo environment.

It still cannot override risk.

---

## 39. Observability Before Automation

Do not deploy unattended automation before we can determine:

* why it traded;
* why it did not trade;
* current risk state;
* current drawdown;
* current mode;
* model version;
* strategy version;
* current open exposure.

Opaque automation is unacceptable.

---

## 40. Hosting

Initial development may run locally.

Later unattended demo operation may use an appropriate reliable hosting environment compatible with the chosen cTrader integration.

Hosting decisions should prioritize:

* stable connectivity;
* correct platform support;
* secure secrets;
* logging;
* restart behavior;
* reasonable latency.

Do not introduce complex cloud infrastructure unnecessarily.

---

## 41. Local Machine Limitations

Local operation may be affected by:

* sleep;
* hibernation;
* shutdown;
* network loss;
* Windows updates;
* platform restarts.

Unattended forward testing requires an environment designed to remain available.

---

## 42. Docker

Docker should be introduced only where it provides concrete value.

Suitable future uses may include:

* PostgreSQL;
* supporting .NET services;
* reproducible research services.

Do not force cTrader platform components into Docker if the trading platform/runtime does not support that architecture appropriately.

---

## 43. Deployment Artifacts

Generated deployment outputs should not pollute source control unnecessarily.

Ignore:

* bin/
* obj/
* temporary logs;
* local secrets;
* local database volumes;
* generated transient model artifacts where appropriate.

---

## 44. Version Information

A running system should expose or log enough information to identify:

* application version;
* Git commit where practical;
* StrategyVersion;
* risk-policy/configuration version;
* ModelVersion;
* FeatureSchemaVersion.

This is essential for reproducible research.

---

## 45. Strategy Version

Initial:

`TrendPullbackV1`

Every executed or simulated trade should eventually be attributable to a strategy version.

---

## 46. Configuration Snapshots

Significant backtests and forward tests should retain a safe snapshot of:

* risk options;
* strategy options;
* execution assumptions;
* model configuration.

Never include secrets in configuration snapshots.

---

## 47. Feature Flags

Potential safe feature controls:

* EnableTrading
* EnableMlFilter
* EnablePersistence
* EnableShadowMode

Dangerous functionality should require explicit opt-in.

---

## 48. Live Mode Guard

Future Live mode should require multiple deliberate conditions.

Conceptually:

`Mode == Live`

AND

`EnableLiveTrading == true`

AND

`Account validated as expected`

AND

`Explicit live configuration present`

This is defense in depth.

Autonomous development must leave these conditions unavailable or disabled.

---

## 49. Live Account Confirmation

Before any future live deployment, require a separate human-reviewed checklist.

The checklist should include:

* broker/account verified;
* strategy validation reviewed;
* demo forward test reviewed;
* drawdown reviewed;
* risk values reviewed;
* starting capital approved;
* position sizing verified;
* emergency procedures understood;
* logs/monitoring working.

This is outside the current implementation scope.

---

## 50. Initial Live Validation

If live testing is ever approved later, it should begin with deliberately limited capital and conservative risk.

Historical or demo performance must never justify immediately scaling position size aggressively.

This future stage requires a separate decision.

---

## 51. Deployment Testing

Automated tests should cover where practical:

* default mode is non-live;
* missing configuration blocks execution;
* invalid configuration blocks execution;
* environment mismatch blocks execution;
* stale data blocks execution;
* emergency shutdown blocks execution;
* duplicate signal does not duplicate order;
* reconnect path does not duplicate existing position;
* restart reconstructs relevant risk state;
* unrelated positions are not modified;
* ML failure does not create uncontrolled execution.

---

## 52. Failure Injection

Where practical, test simulated failures:

* broker rejection;
* timeout;
* stale market data;
* database unavailable;
* ML model unavailable;
* malformed account state;
* invalid volume;
* invalid stop;
* network interruption.

The expected response must favor safe failure.

---

## 53. Operational Runbook

Before unattended demo deployment, document basic operational actions:

* start;
* stop;
* view current mode;
* inspect logs;
* inspect Risk Engine state;
* identify open bot positions;
* trigger emergency disable;
* recover after connection failure;
* restart safely.

This may later become:

`docs/runbook.md`

if necessary.

---

## 54. Alerting

Future unattended deployment may support alerts for:

* hard drawdown shutdown;
* daily loss shutdown;
* repeated broker errors;
* application crash;
* stale market data;
* database failure;
* model load failure.

Alerting must not automatically weaken risk controls.

---

## 55. Database Backup

When persistent research data becomes valuable, establish an appropriate PostgreSQL backup strategy.

The first implementation does not require enterprise disaster-recovery complexity.

However, important research/trade history should not exist in only one fragile local database.

---

## 56. Logs Retention

Logs should have a retention strategy.

Avoid:

* unlimited disk growth;
* deleting critical research evidence immediately.

Retention requirements may evolve during forward testing.

---

## 57. Security

Apply least privilege.

The trading component should receive only the permissions required to perform its approved role.

Do not expose unnecessary network endpoints.

Do not create unauthenticated administrative controls.

---

## 58. Dependencies

Third-party dependencies must be purposeful.

Avoid unnecessary packages with unclear value.

Keep major dependencies reasonably current through deliberate upgrades and testing.

Do not perform uncontrolled mass upgrades immediately before important validation runs.

---

## 59. Build Gate

Before a deployment candidate:

`dotnet restore`

`dotnet build`

`dotnet test`

must succeed for applicable solution components.

Critical warnings should be reviewed.

---

## 60. Research Gate

Before demo order execution:

* Strategy V1 implemented;
* Risk Engine implemented;
* automated tests passing;
* backtesting operational;
* no known critical risk defects;
* shadow-mode integration tested;
* configuration verified.

---

## 61. ML Gate

Before ML affects demo execution:

* deterministic baseline established;
* historical ML evaluation completed;
* leakage tests pass;
* shadow-mode predictions collected;
* model compatibility validated;
* model version recorded.

---

## 62. No Profitability Guarantee

Deployment documentation, logs, reports, and user-facing messages must never state that GoldAiTrader guarantees:

* profit;
* fixed returns;
* maximum losses that cannot be exceeded;
* permanent strategy validity.

The system is trading research software operating in uncertain markets.

---

## 63. Software Complete vs Deployment Ready

These statuses are different.

Possible state:

`SOFTWARE COMPLETE`

but:

`NOT READY FOR DEMO DEPLOYMENT`

Possible state:

`DEMO DEPLOYMENT READY`

but:

`STRATEGY NOT VALIDATED`

Possible state:

`STRATEGY VALIDATED FOR DEMO RESEARCH`

but:

`NOT APPROVED FOR LIVE TRADING`

These distinctions must remain explicit.

---

## 64. Final Report

`docs/final-report.md`

must include deployment readiness information such as:

* supported modes;
* configuration requirements;
* demo safety status;
* known operational risks;
* broker integration status;
* persistence status;
* ML status;
* monitoring status;
* test results;
* unresolved deployment limitations.

---

## 65. Autonomous Completion Boundary

The development workflow may safely complete:

* code;
* tests;
* backtesting infrastructure;
* documentation;
* safe/demo adapters;
* research tooling;
* configuration templates.

Development must stop before any action requiring:

* actual live-account credentials;
* live-money activation;
* irreversible broker action;
* real-money deployment.

---

## 66. Final Autonomous State

The ideal autonomous-development end state is:

`SOFTWARE COMPLETE`

`TESTS PASSING`

`BACKTESTING COMPLETE`

`OUT-OF-SAMPLE RESULTS REPORTED`

`ML RESULTS REPORTED`

`DEMO-READY`

`LIVE TRADING DISABLED`

At this point, the user performs final acceptance testing.

---

## 67. Core Principle

Deployment safety must fail closed.

When GoldAiTrader cannot establish that execution is safe and correctly configured:

`DO NOT TRADE`
