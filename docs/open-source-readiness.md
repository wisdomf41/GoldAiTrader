# Open Source Readiness

GoldAiTrader is maintained as a contributor-friendly C#/.NET project with automated validation and repository safeguards.

## Repository Safeguards

The public repository includes:

- GitHub Actions CI for restore, build, and test validation
- a protected `main` branch
- pull requests required before changes are merged
- the `build-and-test` CI check required before merge
- branches required to be up to date with `main`
- unresolved review conversations required to be resolved
- force pushes blocked on `main`
- deletion of `main` restricted
- Dependabot vulnerability alerts and security updates
- Dependabot malware alerts
- secret scanning performed with Gitleaks before publication

## Contributor Resources

Contributors should review:

- `README.md`
- `CONTRIBUTING.md`
- `CODE_OF_CONDUCT.md`
- `SECURITY.md`
- `.github/pull_request_template.md`
- `.github/ISSUE_TEMPLATE/`

## Development Workflow

Public changes should normally follow this workflow:

1. Create a focused feature, fix, or documentation branch from `main`.
2. Make and locally validate the change.
3. Commit and push the branch.
4. Open a pull request targeting `main`.
5. Allow GitHub Actions to run `build-and-test`.
6. Resolve any review conversations.
7. Merge only after required checks pass.
8. Return to `main` and pull the merged result.

Direct development on the protected public `main` branch should be avoided.

## Trading Safety

Repository workflow protections complement GoldAiTrader's runtime safety rules.

Changes affecting strategy, risk, execution, broker adapters, authentication, or reconciliation should preserve the project's fail-closed and demo-first principles and should not weaken existing trading safeguards.