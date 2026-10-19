# XAUUSD real-data research workflow

This workflow is research only. It neither enables external execution nor establishes profitability.
No real-data validation result is supplied with the implementation. The first evidence run remains
pending trustworthy data collection and broker calibration. Synthetic fixtures test infrastructure only.

## 1. Enable capture locally

Use the existing authenticated Demo terminal installation in
[the telemetry bridge instructions](../platforms/mt5/GoldAiTraderBridge/README.md#installation).
Keep the existing private bridge identity, symbol mapping and secret configuration outside Git.
Do not copy those settings into a research file. Capture adds just one opt-in setting to that host.
From the repository root in the same PowerShell session used to start BridgeHost:

```powershell
New-Item -ItemType Directory -Force ./research-output/capture | Out-Null
$env:MT5Bridge__ResearchCaptureDirectory = (Resolve-Path ./research-output/capture).Path
$env:MT5Bridge__ExternalExecutionEnabled = 'false'
dotnet run --project src/GoldAiTrader.MT5.BridgeHost -c Release
```

The capture directory must be explicitly set and absolute. A blank or relative directory is invalid;
leaving the variable unset disables capture. An external restricted local directory is also supported.
Use one writer per archive and sufficient local disk space. Keep loopback binding `127.0.0.1` and
all existing authentication settings. Never enable external execution to collect telemetry.
To disable capture on a subsequent host start:

```powershell
Remove-Item Env:MT5Bridge__ResearchCaptureDirectory -ErrorAction SilentlyContinue
```

## 2. Collect completed candles

Attach the existing telemetry-only EA in the configured Demo terminal with the exact mapped gold
symbol and completed M5/M15 publication enabled. No terminal source change is required. Confirm
fresh authenticated heartbeat, account, symbol and position snapshots make readiness healthy.
The host captures only accepted XAUUSD M5/M15 completed bars, with receipt time as availability.
Compare sample OHLC, tick volume, spread and UTC interval boundaries against the terminal, including
broker-server UTC conversion. Do not assume a past timezone check covers future DST transitions.

Each accepted candle creates one immutable JSON file. Private envelope/account/bridge fields are
not part of that schema. Storage errors are logged independently of telemetry readiness: healthy
readiness does not prove successful capture. Check file growth, disk permissions and capture logs.
This EA sends the latest completed candle; it is not a historical downloader and does not backfill
outages. Preserve gaps for review; never interpolate candles to make gates pass.

Collect both streams over enough sessions for the selected plan and independent trade sample.
The default plan needs 1,200 training + 600 validation + 600 out-of-sample M5 bars per fold and
three complete disjoint folds (7,200 M5 bars before calendar gaps); it also requires causal M15
warm-up, at least 30 validation and test trades per fold, and the other existing gates. Counts alone
cannot guarantee sufficient evidence or strategy acceptance. Stop BridgeHost before exporting an
immutable snapshot. Use a new archive for a later collection segment or carefully preserve the
stopped snapshot. Do not export while a writer is adding files.

## 3. Export and audit

Create the output directory if it does not exist. Choose and record a UTC AsOf at or after every
captured AvailableAtUtc. It is a fixed research cutoff, not inferred from the current clock.
Set `$asOfUtc` to your chosen ISO-8601 string ending in `Z` or `+00:00` before this command:

```powershell
dotnet run --project src/GoldAiTrader.Research -c Release -- export ./research-output/capture ./research-output/xauusd.dataset.json $asOfUtc
dotnet run --project src/GoldAiTrader.Research -c Release -- init ./research-output/broker.research.json
dotnet run --project src/GoldAiTrader.Research -c Release -- audit ./research-output/xauusd.dataset.json ./research-output/broker.research.json ./research-output/xauusd.audit.json
```

The exporter validates every top-level `*.json` file in the archive. Every file must contain exactly
one valid Mt5Telemetry candle. Mixed sources/symbols, duplicates (even identical copied files),
conflicts, malformed records and observations after AsOf fail closed. Temporary publication files
are ignored. Ordering is availability, timeframe, then open time; unchanged archives and cutoffs
produce identical canonical bytes and SHA-256. Export destinations must be outside the archive.
Do not put unrelated JSON files or nested capture directories in an archive.

Every output uses a flushed temporary file followed by a same-directory atomic rename. Existing
destinations are never overwritten, including audit/configuration/report files. Parent directories
must already exist. Use new filenames for new snapshots; keep old evidence intact. Files beneath
`research-output/` are Git-ignored. If using other paths, keep them outside the repository or arrange
an explicit local ignore before collecting data. Do not commit market data or local calibration.

The export summary reports fingerprint, broker symbol, source, per-timeframe counts and first-open /
last-close UTC timestamps. Export may succeed for an incomplete but structurally valid archive;
its audit summary states gaps and evidence shortages. Export success is not permission to backtest.
The separate audit writes all findings and returns a failure exit code until its prerequisites pass.

The audit distinguishes:

- **HardInvalid:** schema/header, UTC, timeframe alignment, OHLC, negative spread/volume, availability,
  ordering, duplicate/conflicting/overlapping candles, or candles contradicting scheduled closures.
- **ReviewRequired:** unexplained gaps, with exact timeframe and interval. Weekends and holidays
  are not accepted automatically. Declare only independently verified broker closure intervals.
- **InsufficientEvidence:** missing causal warm-up, no possible folds, or fewer folds than policy.

It reports format/source/AsOf, raw input SHA-256, canonical dataset fingerprint for valid data,
M5/M15 counts and intervals, elapsed hours (including gaps) versus observed candle hours,
plan/fold count and warm-up. Invalid JSON has only a raw hash and safe bounded schema error;
untrusted raw strings are not echoed. A canonical fingerprint normalizes JSON whitespace; the raw
hash identifies the exact input bytes. Data validity does not authenticate a source label or establish
that an imported provider is accurate. HistoricalImport must follow the existing canonical schema
and provenance review; the capture exporter never relabels imported or synthetic data as telemetry.

## 4. Calibrate costs and contract

Edit the generated `broker.research.json` locally. All null fields must be filled deliberately:

| Field | Required meaning / evidence |
| --- | --- |
| BrokerSymbol | Exact broker symbol matching the dataset; never an account identifier |
| InitialCapital | Chosen USD research capital per independent fold |
| Calibration.FallbackSpread | Conservative measured bid/ask difference in price units when a candle has no spread |
| CommissionPerLotPerSide | USD commission per lot per side from the broker schedule; convert round-trip quotes explicitly |
| SlippagePricePerSide | Documented adverse price-unit assumption informed by broker/demo observations |
| SpreadMultiplier | Explicit base spread multiplier, at least 1 |
| TickSize | Broker symbol minimum price increment |
| TickValuePerLot | USD tick value for one lot |
| ContractSize | Underlying units per lot |
| MinimumVolume / MaximumVolume / VolumeStep | Broker lot bounds and step |
| MinimumStopDistance | Minimum distance in price units; convert terminal points using its point size |

All rows from CommissionPerLotPerSide through MinimumStopDistance belong inside Calibration.
Obtain contract values from the terminal Symbol Specification and independently verify the units.
No broker values are provided by the tool. Unknown values remain null and produce CalibrationRequired;
zero must be an explicit verified choice, never a substitute for unavailable information. A demo fill
is not a guarantee of live slippage. Preserve supporting observations privately without identifiers
in research JSON. Version 1 supports only linear USD-quoted contracts in USD research currency:
`TickValuePerLot == TickSize * ContractSize`. Unsupported currency conversion fails validation.
Volume limits must align with the step; costs must be nonnegative; tick/contract/volume values positive.

Keep Plan, Policy, Strategy and Risk fixed before examining results. Defaults reuse existing policy
and TrendPullbackV1; changing them is an explicit research decision. ScheduledClosures is an array
of `{ "StartUtc": ..., "EndUtc": ... }` with ordered non-overlapping UTC intervals aligned to M15.
An observed gap is explained only by an exact declared interval. Obtain calendars independently;
do not infer closures from profitable outcomes. Re-run audit to a new output after calendar review.
The audit can run before cost calibration because it needs only plan, minimum folds and closures.
Use `-` as its configuration argument to inspect with the default plan and no declared closures.

Unknown and duplicate JSON properties are rejected, including account IDs, passwords and tokens.
There is no free-form metadata field in the research configuration. Do not paste a bridge settings
file, account snapshot or authentication envelope into it.

## 5. Run fixed research validation

```powershell
dotnet run --project src/GoldAiTrader.Research -c Release -- run ./research-output/xauusd.dataset.json ./research-output/broker.research.json ./research-output/xauusd.validation.json
```

The runner audits, validates calibration, builds folds, and calls the existing TrendPullbackV1
walk-forward engine. Invalid data, unexplained gaps or insufficient warm-up/folds stop simulation.
Missing calibration (`-` as configuration) produces a failed report with CalibrationRequired.
Malformed/unreadable configuration fails before simulation with a safe console error.
A valid run still fails when trade samples, risk, performance, baseline or robustness gates fail.
SyntheticFixture always fails the evidence/methodology gate, even with otherwise passing metrics.
HistoricalImport and Mt5Telemetry have identical evidence requirements beyond their provenance label.

The report retains the audit, full validated configuration/settings, folds, trades, aggregate metrics,
engine/strategy/implementation identity and four gate groups. No input paths or private identity
are recorded. Console output includes Passed, DataIntegrity, LeakageAndReproducibility, RiskLimits,
StrategyPerformance, folds, OOS trades, net P/L, return, drawdown, profit factor, expectancy,
baseline comparison, validation-only cost/parameter stress and every gate failure reason.
NotEvaluated is explicit when a precondition prevents simulation. No trades is not unbounded profit.
Repeated inputs and the same binaries produce identical report bytes. Keep the source revision,
binaries, dataset, configuration and report together privately; the assembly fingerprint changes
when implementation binaries change. Read the [model limitations](xauusd-research-validation.md).

Exit codes: `0` command success (for run: every gate passed), `2` audit/validation failed,
`1` invalid command/input/configuration or I/O failure. All outputs are local. A research pass is
neither proof of a durable edge nor production readiness. Do not tune repeatedly until it passes.

## Untouched final holdout

This phase intentionally has no automatic holdout evaluation or optimization. Use this exact boundary:

1. Before inspecting strategy results, record the development interval and a later, disjoint final
   holdout interval in a private research protocol. Boundaries are UTC and aligned to M15.
2. Stop collection/export at the development boundary. Store later holdout capture in a separate
   restricted archive, never in the development dataset. If licensed historical material includes
   both periods, have the data custodian partition it before supplying the development dataset.
   Preserve provenance and record hashes of both partitions; do not relabel sources.
3. Only the development dataset goes through this tool's planning, validation, cost stress and
   parameter sensitivity. Its fold OOS windows are development evidence, not the untouched final holdout.
4. Freeze strategy, risk, costs, thresholds, calendar and exact binaries after development validation
   passes. Arrange one separately reviewed final evaluation with fixed settings and only earlier
   history for warm-up. Do not feed the holdout to this runner, which includes sensitivity passes.
5. Inspect the final holdout once. A failure is a failure; do not retune against it. Any material
   revision after viewing it needs a new untouched period. Forward Demo evidence is still required.

Do not reserve a holdout by merely promising to ignore later rows in a combined dataset: this runner
uses every complete planned fold in its input. Physical dataset separation is the required workflow.
