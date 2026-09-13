## Summary

Describe what this pull request changes and why.

## Type of Change

- [ ] Feature
- [ ] Bug fix
- [ ] Refactoring
- [ ] Documentation
- [ ] Tests
- [ ] Infrastructure / CI

## Validation

- [ ] `dotnet build` passes
- [ ] `dotnet test` passes
- [ ] Existing behavior has been regression-tested
- [ ] Documentation has been updated where necessary

## Trading Safety

For changes affecting strategy, risk, execution, adapters, or live-trading boundaries:

- [ ] Risk controls are not bypassed
- [ ] Mandatory stop-loss behavior is preserved
- [ ] Live trading is not enabled automatically
- [ ] Indeterminate execution outcomes are not retried unsafely
- [ ] Reconciliation and ownership rules remain intact

## Additional Notes

Add any implementation details, limitations, screenshots, or follow-up work here.