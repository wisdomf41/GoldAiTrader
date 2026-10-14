# XAUUSD research validation

This is an offline research foundation, not approval to trade. External execution remains disabled.
The existing TrendPullbackV1, indicator implementations, risk engine, health supervision, and EA
execution behavior are unchanged. Strategy validation requires broker data and independent review.

## Dataset and capture

ResearchDataset is format version 1: one canonical XAUUSD / broker-symbol pair, a bounded source
enum, a fixed UTC as-of cutoff, and completed M5/M15 candles. OHLC is **bid-price OHLC**, prices
and spread are in USD per ounce, volume is the provider's optional candle volume (not order lots).
AvailableAtUtc records when a candle became available; historical importers must not backdate it
when their source has publication delays. Capture uses receipt time, not the bridge envelope's identity.
CloseTimeUtc is the exclusive end of the interval. The input order is availability order, chronological
within each timeframe. Duplicate, conflicting, overlapping, misaligned, non-UTC, future-as-of,
negative-volume/spread, and malformed price records are rejected. Unknown, duplicate, and missing fields are rejected on load.

The broker-symbol allowlist supports XAUUSD or GOLD plus a short alphabetic/punctuation suffix; extend it
deliberately for other naming conventions. Do not put customer identifiers in symbols or paths.
Source is provenance category, not verification of the provider's historical accuracy. Retain raw
licensed source material separately and review import mappings, timezone, bid basis, and completeness.

Configure an absolute local directory with MT5Bridge__ResearchCaptureDirectory to opt in.
Leave it unset to disable capture. Use a restricted storage directory, preferably outside the repository.
research-output/ is ignored for local experiments. No credentials or account/session identifiers
are copied. Capture runs only after bridge-state acceptance and only for supported XAUUSD M5/M15 bars.

Each accepted candle has an immutable JSON file named by a hash of its stream/time key. Publication
uses a flushed temporary file and atomic rename on the same filesystem. Exact retries are idempotent;
conflicts never overwrite original data. A failed capture logs once until recovery, independently
of telemetry readiness. There is no replay queue: operators must repair storage and obtain missing
candles from historical data. Monitor disk space, permissions, retention, and capture error logs.
Stop capture before exporting a final evidence snapshot. The archive lock coordinates a single
process; do not share a writable archive between hosts. Temporary files are excluded from export.

ResearchBarArchive.Export(asOfUtc) assembles and validates a snapshot. ResearchDatasetFile.Save
writes a new file without replacing an existing snapshot; Load validates it; Fingerprint returns
SHA-256 over deterministic UTF-8 JSON. Fix the cutoff for repeatable exports. Neither datasets nor
reports should be checked in with large market histories.

## Running research

The public APIs in GoldAiTrader.Backtesting accept ResearchDataset directly:

    var dataset = ResearchDatasetFile.Load(File.ReadAllBytes(datasetPath));
    var settings = new ResearchRunSettings(
        10000m,
        new SymbolSpecification(.01m, 1m, .01m, 100m, .01m, .10m,
            dataset.BrokerSymbol, "XAUUSD", 100m),
        new ResearchCosts(FallbackSpread: .30m, CommissionPerLotPerSide: 3.50m,
            SlippagePricePerSide: .05m),
        new TrendPullbackOptions(), new RiskOptions());
    var folds = WalkForwardValidation.Plan(dataset,
        trainingBars: 1200, validationBars: 600, outOfSampleBars: 600);
    var report = WalkForwardValidation.Run(dataset, settings, folds, new ValidationPolicy());
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
        new JsonSerializerOptions { WriteIndented = true }));

These example contract/cost values are illustrative, not broker calibration or evidence of profitability.
Supply broker specifications and measured cost assumptions before interpreting performance.
The version-1 currency model is a linear USD-quoted contract in a USD research account:
TickValuePerVolumeUnit must equal TickSize * ContractSize. Other account currencies need explicit
conversion support before use. Contract size is units per lot, commission is USD per lot per side,
slippage is adverse price units per side. Spread uses the most recent causally available observation
or the explicitly supplied fallback, multiplied by the stress factor. Zero costs require an explicit
choice; they are unsuitable as primary real-world evidence.

Reports retain dataset fingerprint, engine/strategy versions, a fingerprint of the participating assembly module IDs, all strategy/risk/cost/contract settings,
window boundaries, deterministic trades, equity observations, failures, and policy. Retain the source
revision and binaries alongside the report: a version string alone cannot identify uncommitted edits.
Random strategy signal IDs are excluded from research results. There is no wall-clock/random input
to the runner. Identical inputs produce identical JSON reports.

## Causality and execution assumptions

Features use only candles whose AvailableAtUtc is no later than the next M5 open. M5 EMA20/50,
ATR14 and RSI14 and M15 EMA50/200 and ADX14 reuse existing indicator formulas. No label or
future OHLC is accepted as a feature. 200 completed M15 candles are required; warmup candles
do not generate trades or profit. Features are stale after 10 minutes for M5 or 30 minutes for M15.
A delayed candle may first affect a later entry. Entry uses the next eligible M5 open and adverse
spread/slippage, never the prior candle's closing fill. Spread is held constant within that simulated
bar: a candle snapshot cannot reproduce intrabar spread extremes.

Stops, targets, minimum stops, tick value, stepped lot sizing, commission and slippage are applied.
Sizing includes estimated exit slippage and round-trip commission within the risk budget. Actual
gap losses can exceed planned risk. Stop wins ambiguous bars; adverse stop gaps fill at the worse
opening price; favorable target gaps do not receive price improvement. Entries are followed by
that bar's stop/target checks. Positions close at the holding limit or window end. Intrabar exit
timestamps are conservatively recorded at candle close; exposure counts occupied M5 intervals.
No position crosses a validation or test boundary.

Day/week anchors, loss cooldown and peak equity are retained during a run. Reports flag daily,
weekly and drawdown limit breaches, including a conservative intrabar equity lower bound.
OHLC cannot reconstruct event ordering or tick-level drawdown. The equity series includes adverse
bar extrema, capped at executed stops, then closing/liquidation equity; it is not a tick history.
Commission is reserved in marked equity, booked in realized P/L at exit. Swaps, financing, variable
intrabar spreads, partial fills, latency distributions and nonlinear currency conversions are not modeled.
Longer holding periods or those broker features require additional cost evidence/support.

Unexpected calendar gaps are data failures, including missing warmup candles. Supply broker
closures explicitly through ResearchRunSettings.ScheduledClosures (ordered, non-overlapping
UTC ResearchClosure intervals on M15 boundaries). A gap is accepted only when it exactly matches
a declared closure; candles inside a closure fail data integrity. Obtain these intervals from an
independent broker session/holiday calendar before inspecting outcomes. The runner never infers
a calendar from observed prices. It liquidates at the last candle before a scheduled closure and
waits up to 30 minutes after reopening for fresh causal indicators; prior warmup is still required.
Missing bars outside those intervals fail. Do not manufacture bars to obtain a passing gate.

## Walk-forward protocol and gates

Plan creates disjoint training, validation and out-of-sample windows; all endpoints are UTC and
half-open. Incomplete trailing data is unused. Each fold warms from its own training history.
Training identifies the allowed development period; this foundation does not fit or optimize parameters.
Settings are fixed before the run. Validation history may causally warm test indicators, but neither
validation nor test results select parameters automatically. Do not repeatedly choose parameters
using the final test report; reserve a fresh final holdout when human development decisions change.

Each fold reports validation, test, a fixed long ATR reference (1.5 ATR stop / 2R target with the same
risk/cost constraints), validation-only 1.5x cost stress, and validation-only modest parameter stress
(0.9x pullback tolerance / 1.1x stop distance). Sensitivities diagnose fragility, not confidence intervals.
The baseline is a deterministic reference, not passive gold ownership and not optimized.

Aggregate test metrics combine equally funded independent folds: initial capital is per-fold capital
times fold count; net profits sum, without implying reinvestment through unused intervals.
Per-fold risk checks remain authoritative; aggregate dilution cannot conceal a failing fold.
The report includes capital, net/percentage return, absolute/percentage drawdown, win/loss counts,
win rate, average wins/losses, expectancy, gross profit/loss, profit factor, net R, losing streak,
and interval exposure. With no losses, profit factor is null plus an explicit unbounded flag only
when gross profit is positive; no trades is not an infinite-profit success.

The four independent gate groups are data integrity, leakage/reproducibility, risk, and performance.
Invalid data/windows return categorized failures. Invalid configuration throws before research runs.
Out-of-sample runs are repeated and compared byte-for-byte. Synthetic fixtures cannot qualify as
strategy evidence. Missing warmup, gaps, incomplete windows, empty folds, or insufficient samples
cannot produce a final pass.

Research defaults (configurable, **not production profitability thresholds**) are at least three folds,
30 trades per validation/test fold, profit factor >=1.2 and positive expectancy, positive validation
stress outcomes, and per-fold improvement over the reference. Drawdown is at most 12% and never
looser than the configured risk hard limit, reflecting the existing risk policy. Other defaults cap
one fold at 60% and one trade at 20% of gross positive test P/L. Fold return range, top-one/top-five
trade concentration and stress returns are reported; concentration shares are withheld until the
minimum test sample exists. These heuristics do not establish statistical confidence or independence.

A gate pass is research-policy acceptance only. Real XAUUSD historical and forward-demo data are
still needed to establish bid/ask and timestamp fidelity, broker contract values, realistic costs,
session/calendar coverage, enough independent trades/folds, and persistence under an untouched
holdout and forward demo. Execution authorization remains a separate safety decision.
