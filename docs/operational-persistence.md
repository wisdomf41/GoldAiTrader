# Operational persistence and reconciliation

GoldAiTrader treats operational state as safety-critical state. A process restart must not erase
whether a signal was submitted, whether its broker outcome is uncertain, whether the account is
suspended after consecutive losses, or whether emergency shutdown is active.

## Execution journal and trade history

The execution journal is an operational ledger. It records intent before a broker call and is
separate from the completed-trade history used for research and performance analysis. A journal
row contains the signal and client-correlation identities, canonical symbol, account identifier,
strategy version, direction, requested volume and prices, broker order/position identifiers,
timestamps, submission count, last broker message, and lifecycle state.

Lifecycle states are:

1. Reserved  the signal identity was atomically claimed.
2. Submitted  a broker submission attempt is beginning.
3. Accepted  the broker positively acknowledged the order.
4. Rejected  the broker positively rejected the order.
5. Indeterminate  transport or broker behavior left the outcome unknown.
6. Reconciled  an indeterminate execution was matched to an owned broker position.

SignalId and ClientCorrelationId are unique database identities. BrokerPositionId is unique when
present. Reservation happens before submission, so a new process cannot resubmit an existing
signal. An indeterminate outcome is never converted into a retryable rejection and is never
resubmitted automatically.

## Position ownership

A position is owned only when all of the following are present:

- the ownership tag is GoldAiTrader;
- SignalId, client correlation ID, and strategy version are populated.

A position with no ownership metadata is treated as manual/unowned. Partial or contradictory
metadata is ambiguous. GoldAiTrader may close or modify only unambiguously owned positions after
checking the current broker inventory. Manual, missing, and ambiguous positions are fail-closed
and require operator review.

During reconciliation, ownership metadata must also match the durable execution intent: signal,
correlation, strategy version, canonical symbol, direction, and volume.
Reconciliation is scoped to the verified broker account identifier. The journal query filters by
account in the persistence provider, so rows for Account A are never compared with Account B's
position inventory. An empty account identifier is an error. If a journal row already has a
BrokerPositionId, the matching position must have that exact identifier; a disagreement is a
blocking ambiguity even when SignalId matches.

## Startup reconciliation

Demo submission remains blocked until startup reconciliation completes safely. Reconciliation
compares durable journal rows with the current broker position inventory and produces explicit
outcomes:

- accepted/reconciled row plus one matching owned position: safe;
- indeterminate row plus one matching owned position: mark Reconciled;
- accepted row with no position: block;
- indeterminate row with no position: block;
- owned position with no journal row: block;
- multiple matches, partial ownership, or mismatched metadata: block;
- unrelated manual position: report it but do not manage it or block solely because it exists.

No discrepancy is resolved by silently deleting a journal row, inventing a fill, closing a
position, or submitting another order.

External execution uses one mandatory readiness boundary composed from account-scoped startup
reconciliation and current operational health. There is no production always-ready
implementation. The readiness boundary rejects an account other than the one reconciled and
blocks when reconciliation is incomplete or unsafe, durable safety state is unrestored,
emergency shutdown is active, shutdown is in progress, market data is stale, broker connectivity
is unhealthy, trading is disabled, or execution is not explicitly Demo.

The startup coordinator executes the required sequence in this order:

1. invalidate any earlier reconciliation readiness;
2. restore durable operational safety state;
3. restore persisted RiskState;
4. obtain and verify a connected, explicitly identified Demo account;
5. reconcile that account's journal and broker positions;
6. apply the completed reconciliation report;
7. verify current operational health;
8. allow external Demo execution.

An exception or unsafe result at any mandatory step leaves external execution blocked.

## Risk and emergency state

Consecutive-loss count and SuspendedUntil are persisted per scope. PersistedRiskSession rejects
executable assessments until restoration succeeds, then supplies the restored RiskState to
RiskEngine on every assessment. Closed-trade results are passed through RiskStateTracker and the
updated state is saved before the session publishes it in memory. A failed reload returns the
session to the unrestored, fail-closed state. Emergency shutdown is also persisted per scope.
When a durable safety repository is configured, OperationalGuard refuses new trades until
RestoreSafetyStateAsync completes successfully.

Emergency shutdown survives process restart. It can be cleared only through the explicit
ClearEmergencyShutdownAsync administrative path; clearing updates the durable row rather than
deleting the audit state.

## Database lifecycle

The PostgreSQL schema is managed by Entity Framework Core migrations. The design-time factory
reads GOLDAITRADER_DESIGN_CONNECTION; its fallback is a local, credential-free design string.
Production and demo operators must provide an environment-specific connection securely.

Generate or review migration scripts before applying them. Application startup must not call
EnsureDeleted, recreate the database, or silently discard operational rows. The initial migration
creates the existing research tables plus ExecutionJournal, RiskStates, and
OperationalSafetyStates with their keys and unique indexes.

The startup-safety integration changes runtime queries and orchestration only; it adds no schema.

## Operator recovery sequence

1. Keep Demo submissions disabled.
2. Restore durable risk and emergency state.
3. Verify the broker account identity and Demo environment.
4. Load the current broker position inventory.
5. Run reconciliation and review every blocking discrepancy.
6. Resolve discrepancies through an audited operator decision.
7. Rerun reconciliation.
8. Apply the readiness gate only when the report is safe.
9. Enable Demo submission; live trading remains prohibited.

A safe result means the software can determine its operational state. It does not validate the
strategy or imply profitability.
