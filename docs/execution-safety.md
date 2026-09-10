# Demo Account and Execution Safety

External execution fails closed. An unpopulated `AccountSnapshot` has environment `Unknown`;
missing metadata is never treated as Demo. Before submit, close, or modify, the broker-neutral
executor requires all of the following:

- a non-empty account identifier;
- account environment explicitly reported as `Demo`;
- a healthy broker connection;
- `TradingEnabled = true`;
- execution mode explicitly `Demo`;
- `DemoOnly = true`.

Submission also requires a durable execution journal and the mandatory external-readiness
composition. That composition accepts only the account identifier covered by a safe startup
reconciliation report and a currently healthy operational guard. It therefore enforces restored
durable safety state, no emergency or orderly shutdown, fresh market data, broker connectivity,
enabled trading, and Demo mode at the final gateway boundary.

Live and unknown accounts are rejected. The cTrader adapter obtains normalized account metadata
and connection state through `ICTraderPlatformClient`; demo-named methods are not proof of the
actual connected account. No real cTrader client or broker connectivity is implemented.

## Close and modify

Close and modification requests use the same account and configuration verification as new
orders. They cannot bypass Demo-only or readiness restrictions. They also query the current
position inventory and permit management only for positions carrying complete, unambiguous
GoldAiTrader ownership metadata.

## Retry semantics

Results default to `NotRetryable`. The executor retries only a definitive rejection explicitly
marked `SafeToRetry`, meaning the adapter can prove no order was submitted or accepted. Duplicate,
indeterminate, ordinary rejection, and exception/timeout outcomes are not retried. Exceptions are
reported as `Indeterminate` because the broker outcome may be unknown.

## Post-fill slippage

Slippage is evaluated after the gateway response. If an accepted fill exceeds the configured
limit, the result remains `Accepted`, retains its broker order identifier, and carries
`ExecutionSafetyStatus.SlippageLimitExceeded`. This is an alert condition; it is not rewritten as
a fictional pre-execution rejection and does not trigger automatic recovery trading.
