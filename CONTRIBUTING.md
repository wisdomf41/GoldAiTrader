# Contributing to GoldAiTrader

Thank you for your interest in contributing to GoldAiTrader.

GoldAiTrader is a modular C#/.NET automated trading research and execution platform focused on safety, testability, broker independence, and controlled risk.

## Development Principles

Contributions should preserve these principles:

- Safety first and fail closed by default.
- Live trading must never be enabled automatically.
- Trading-platform-specific logic should remain behind adapters or gateway boundaries.
- Execution behavior must remain deterministic and testable.
- Risk controls must not be bypassed.
- Indeterminate broker outcomes must require reconciliation.
- Secrets, credentials, and private connection details must never be committed.

## Requirements

For normal development you need:

- .NET 10 SDK
- Git

PostgreSQL is required only when working with persistence or database integration.

MetaTrader 5 and cTrader are not required for normal unit-test development.

## Build and Test

Restore dependencies with `dotnet restore`.

Build the solution with `dotnet build`.

Run all tests with `dotnet test`.

All existing tests should remain green before a pull request is submitted.

## Branches

Create a focused branch for each change.

Examples:

- `feature/mt5-integration`
- `feature/backtesting-report`
- `fix/execution-reconciliation`
- `docs/architecture-update`

## Code Contributions

Please:

- Keep changes focused.
- Follow the existing project architecture.
- Add tests for meaningful behavior changes.
- Update documentation when architecture or behavior changes.
- Preserve broker-independent domain logic.
- Prefer explicit, readable implementations over unnecessary complexity.

## Execution and Risk Safety

Changes must not:

- Enable Live trading by default.
- Bypass Demo-only execution safeguards.
- Remove mandatory stop-loss protection.
- Weaken account-environment verification.
- Weaken execution readiness checks.
- Allow uncontrolled retries after an indeterminate broker outcome.
- Bypass position ownership or reconciliation rules.
- Expose the local MT5 bridge outside its intended loopback boundary without an approved design change.

Execution-related contributions should include tests covering failure and recovery scenarios.

## Pull Requests

A pull request should explain:

1. What changed.
2. Why the change is needed.
3. Any architecture or safety impact.
4. Tests added or updated.
5. Validation performed.

Before submitting, run:

- `dotnet restore`
- `dotnet build`
- `dotnet test`

## Documentation

Update the relevant documentation when changing:

- architecture
- strategy behavior
- risk policies
- backtesting
- persistence
- execution safety
- platform adapters
- MT5 bridge behavior
- machine-learning components

## Project Scope

GoldAiTrader is being developed incrementally. Some integrations are intentionally incomplete until they can be implemented and validated safely.

Please do not bypass safety boundaries simply to make an unfinished integration appear complete.
