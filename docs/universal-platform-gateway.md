GoldAiTrader Universal Trading Platform Gateway

Purpose

GoldAiTrader is designed as a broker-neutral and platform-neutral trading engine.

The trading strategy, risk management, execution safety, persistence,
reconciliation, backtesting, and machine-learning components must not depend on
a specific trading platform such as cTrader or MetaTrader 5.

Trading platforms are treated as replaceable adapters.

The architecture is:

                     GoldAiTrader Engine
                            |
                Universal Platform Gateway
                            |
          +-----------------+-----------------+
          |                 |                 |
      MT5 Adapter       cTrader Adapter    REST/FIX Adapter
          |                 |                 |
        Broker            Broker            Broker

GoldAiTrader owns trading decisions.

Platform adapters only provide access to external trading-platform capabilities.

Core Principle

GoldAiTrader is the trading engine.

A platform adapter is a connector.

Platform-specific implementation details must not leak into:

strategy logic

risk logic

backtesting

machine learning

durable execution persistence

reconciliation concepts

operational safety rules

This allows the same trading engine to operate through different platforms

without rewriting its strategy or risk-management logic.

Universal Platform Project

The universal gateway contracts are located in:

src/GoldAiTrader.Platform

The project depends on:

GoldAiTrader.Core
GoldAiTrader.Execution

It composes existing broker-neutral contracts instead of duplicating them.

The primary abstraction is:

ITradingPlatformGateway

A platform gateway can expose:

IMarketDataProvider
IHistoricalMarketDataProvider
ITradingAccountProvider
ISymbolSpecificationProvider
IPositionProvider
IExecutionGateway
IExecutionMetadataSupport

Each platform adapter supplies only the contracts it genuinely supports.

Platform Identity

A platform adapter exposes normalized identity metadata through

PlatformIdentity.

The identity can describe:

Platform name
Adapter name
Adapter version
Broker name
Connection identifier

Platform names are not represented by a closed platform enum.

This allows future platforms to be introduced without changing universal

GoldAiTrader code.

For example, future adapters could represent:

MetaTrader 5
cTrader
REST broker API
FIX gateway
Custom institutional gateway

The universal layer does not require special-case logic for these names.

Platform Capabilities

Every adapter advertises explicit capabilities through

PlatformCapabilities.

Current capabilities include:

StreamingMarketData
HistoricalBars
AccountInformation
AccountEnvironmentClassification
CanonicalSymbolMapping
SymbolSpecifications
PositionInventory
SubmitMarketOrders
ClosePositions
ModifyPositions
StopLoss
TakeProfit
ClientCorrelationIds
OwnershipMetadata
ReconnectSupport

Capabilities are deterministic flags.

A capability flag alone is not sufficient when a supporting provider contract

is required.

For example:

HistoricalBars

requires:

IHistoricalMarketDataProvider

and:

ClientCorrelationIds
OwnershipMetadata

require:

IExecutionMetadataSupport

This prevents an adapter from claiming important safety capabilities without

providing a concrete implementation contract.

Operating Modes

GoldAiTrader validates platform capabilities according to the current execution

mode.

Backtest

Backtest mode requires only capabilities needed for historical simulation.

It does not require broker order execution.

Examples include:

Historical bars
Canonical symbol mapping

A historical file-replay adapter can therefore be suitable for backtesting

without being capable of submitting broker orders.

Shadow

Shadow mode observes live conditions and evaluates the strategy without

executing trades.

It requires appropriate market, account, and symbol information but does not

require broker order submission.

This allows GoldAiTrader to validate strategy behavior safely before enabling

Demo execution.

Demo

Demo execution requires the full safety-critical capability set needed for:

market data
account information
account-environment verification
symbol normalization
symbol specifications
position inventory
order submission
position closure
position modification
stop loss
take profit
client correlation identifiers
GoldAiTrader ownership metadata

Missing required capabilities cause startup validation to fail closed.

Live

Live execution remains disabled.

Even an adapter reporting every capability does not make Live mode suitable.

The universal gateway must never weaken GoldAiTrader's existing Demo-only

safety restrictions.

Live activation will require a separate future readiness phase and explicit

policy changes.

Fail-Closed Capability Validation

PlatformCapabilityValidator checks whether an adapter satisfies the

requirements for the selected execution mode.

If a required capability is absent, the validator reports it explicitly.

For example:

Missing required platform capability: PositionInventory.

GoldAiTrader does not guess that an unsupported feature exists.

It does not silently degrade external execution.

For executable Demo startup, safety-critical capability failures block startup.

Examples:

No account-environment classification
    -> Demo account cannot be verified
    -> execution blocked

No position inventory
    -> startup reconciliation cannot safely complete
    -> execution blocked

No client correlation support
    -> durable execution identity cannot be safely preserved
    -> execution blocked

No ownership metadata
    -> GoldAiTrader cannot safely prove broker-position ownership
    -> execution blocked

Demo Startup Integration

DemoStartupCoordinator validates the platform before external Demo execution

is considered ready.

The startup flow is conceptually:

Restore durable safety state
        |
Restore persisted risk state
        |
Validate platform capabilities
        |
Verify execution account
        |
Require explicit Demo account
        |
Load broker position inventory
        |
Run account-scoped reconciliation
        |
Apply reconciliation gate
        |
Check operational readiness
        |
READY

If any safety-critical stage fails, startup remains not ready.

The gateway does not bypass:

GatewayTradeExecutor
StartupReconciliationGate
OperationalGuard
PersistedRiskSession
ExecutionReconciliationService

Symbol Normalization

GoldAiTrader uses canonical symbols internally.

For Gold, the strategy may reason about:

XAUUSD

A broker may expose a different symbol such as:

XAUUSD
XAUUSD.a
XAUUSDm
GOLD

These are examples only.

They are not hard-coded as universal assumptions.

Each platform adapter owns its own canonical-to-broker symbol mapping.

The platform adapter converts broker-specific symbol information into

SymbolSpecification.

GoldAiTrader strategy and risk logic continue to operate on the canonical

symbol.

Symbol Specifications

Adapters are responsible for supplying accurate broker-specific symbol

specifications, including appropriate values such as:

tick size
tick value
minimum volume
maximum volume
volume step
minimum stop distance
contract size
trading availability
broker symbol
canonical symbol

These specifications are required by the risk engine for safe position sizing.

GoldAiTrader must not guess missing broker specifications.

Position Ownership

GoldAiTrader must distinguish its own positions from:

manual positions
positions created by other EAs
positions created by other systems

Execution ownership uses durable identifiers including concepts such as:

SignalId
ClientCorrelationId
StrategyVersion
OwnershipTag

The default GoldAiTrader ownership tag remains:

GoldAiTrader

Adapters that participate in safe Demo position management must explicitly

support the metadata required by the ownership model.

Unowned positions must not automatically be modified or closed by GoldAiTrader.

Execution Safety

The Universal Platform Gateway does not replace the existing execution-safety

layer.

All existing safety behavior remains required.

This includes:

durable execution journal
idempotent signal reservation
account-scoped reconciliation
position ownership verification
indeterminate execution handling
persistent emergency shutdown
persisted risk state
stale market-data blocking
broker-disconnect blocking
mandatory external execution readiness
Demo-account verification
Live execution disabled

Platform adapters cannot bypass these controls.

Current cTrader Adapter

The existing project:

src/GoldAiTrader.Adapters.CTrader

contains the current cTrader boundary.

CTraderPlatformGateway implements the universal platform contract.

The existing ICTraderPlatformClient remains interface-only.

There is currently no:

cTrader Open API connection
cTrader SDK dependency
OAuth implementation
broker credential handling
real Demo account connection
real broker order submission

These will be implemented only in a later integration phase.

The current cTrader project proves that a platform-specific adapter can fit

inside the universal architecture without changing Core strategy or risk logic.

Future MetaTrader 5 Bridge

MetaTrader 5 uses a different integration model.

The intended architecture is:

MT5 terminal/chart
        |
GoldAiTraderBridge EA
        |
Local authenticated transport
        |
GoldAiTrader.Adapters.MT5
        |
Universal Platform Gateway
        |
GoldAiTrader Engine

The MQL5 EA should remain thin.

Its responsibilities should primarily include:

reading MT5 market/account data
reading symbol specifications
reading positions
forwarding normalized platform messages
receiving already-approved execution instructions
submitting approved orders
reporting broker results

The MQL5 bridge must not become the owner of:

strategy logic
risk policy
ML trade decisions
durable execution state
GoldAiTrader reconciliation policy

Those remain in the C# GoldAiTrader engine.

This preserves one trading brain across multiple platforms.

Future REST Adapter

A broker exposing a REST API can implement the same universal contracts.

Conceptually:

Broker REST API
       |
GoldAiTrader.Adapters.Rest
       |
Universal Platform Gateway
       |
GoldAiTrader Engine

The REST adapter would handle:

authentication
request/response mapping
broker symbols
account retrieval
position retrieval
order transport
broker-specific errors
connection/retry behavior

Universal strategy and risk logic would remain unchanged.

Future FIX Adapter

Institutional or professional broker connectivity can later be implemented

through a FIX adapter.

Conceptually:

FIX Session
    |
GoldAiTrader.Adapters.Fix
    |
Universal Platform Gateway
    |
GoldAiTrader Engine

FIX protocol details must remain inside the adapter.

The rest of GoldAiTrader should continue to use the same platform-neutral

contracts.

Reconnect Support

Reconnect support is represented as an explicit platform capability.

It does not mean GoldAiTrader should immediately resume trading after a

connection loss.

After reconnection, the system must still perform the required safety sequence,

including appropriate:

account verification
position refresh
reconciliation
market-data freshness verification
operational readiness checks

A reconnect must therefore fail closed until the system has restored a trusted

state.

Security Boundary

Platform adapters may eventually require broker credentials, tokens, API keys,

certificates, or local terminal authentication.

Secrets must not be:

committed to Git
hard-coded in source
stored in platform descriptors
included in logs
included in tests

Secrets should later be supplied through appropriate secure configuration.

No broker credentials are required by the current gateway-foundation phase.

Current Status

The Universal Trading Platform Gateway foundation currently provides:

Broker-neutral platform contract
Extensible platform identity
Explicit capability model
Mode-specific capability validation
Fail-closed Demo capability checks
Explicit historical-data contract
Explicit execution-metadata capability support
Canonical symbol boundary
cTrader universal wrapper
Demo startup capability validation
Live execution blocking
Automated gateway tests

At this stage, the architecture contains contracts and test infrastructure only.

It does not yet contain real broker connectivity.

Next Integration Phases

The expected future progression is:

Universal Platform Gateway
        |
        +-- MT5 bridge implementation
        |
        +-- cTrader concrete client
        |
        +-- real Demo account connectivity
        |
        +-- reconnect/reconciliation runtime testing
        |
        +-- Demo forward execution testing
        |
        +-- extended broker adapters
        |
        +-- eventual separate Live-readiness review

Live trading must remain disabled until a dedicated future safety review proves

that the system is ready.