# Security Policy

GoldAiTrader is an algorithmic trading research and execution platform. Security issues affecting authentication, execution safety, broker integration, credentials, risk controls, or trading-state reconciliation should be treated carefully.

## Supported Version

Security fixes are applied to the latest code on the `main` branch.

Older commits, experimental branches, and historical development snapshots are not supported.

## Reporting a Vulnerability

Please do not disclose security vulnerabilities in a public GitHub issue.

Where GitHub private vulnerability reporting is available, use the repository's **Security** section to report the issue privately.

If private reporting is unavailable, contact the repository maintainer privately through GitHub before publishing technical details.

Please include:

- a clear description of the vulnerability
- affected component or project
- steps to reproduce
- expected and actual behavior
- potential security or trading impact
- relevant logs or diagnostic information
- a suggested mitigation, if known

Do not include passwords, API keys, broker credentials, signing secrets, access tokens, or other sensitive information.

## Security-Sensitive Areas

Examples of security-sensitive areas include:

- MT5 loopback bridge authentication
- HMAC signing and verification
- timestamp and nonce validation
- replay protection
- bridge endpoint exposure
- credential and secret handling
- execution command ownership
- order acknowledgement handling
- indeterminate execution outcomes
- reconciliation logic
- risk-control enforcement
- stop-loss enforcement
- environment and account validation
- live-trading enablement boundaries
- persistence of execution state

## Trading Safety

A security fix must not weaken existing trading safeguards.

In particular, changes must not:

- enable live trading automatically
- bypass configured risk limits
- bypass mandatory stop-loss requirements
- retry indeterminate executions as though they were known failures
- bypass position ownership or reconciliation checks
- expose the MT5 bridge beyond the intended loopback boundary
- log or persist secrets in plaintext
- weaken authentication, replay protection, or command validation

## Responsible Disclosure

Please allow reasonable time for investigation and remediation before publicly disclosing a vulnerability.

Security reports made in good faith are appreciated.