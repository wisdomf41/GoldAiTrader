using System.Globalization;
using System.Text.Json;
using GoldAiTrader.Backtesting;
using GoldAiTrader.Core;
using GoldAiTrader.Data;

namespace GoldAiTrader.Research;

public static class ResearchCommand
{
    public static int Execute(string[] args, TextWriter output)
    {
        try
        {
            if (args.Length == 2 && args[0] == "init")
            {
                ResearchConfigurationFile.SaveTemplate(args[1]);
                output.WriteLine("Calibration required: fill every null field using verified broker values before running research.");
                return 0;
            }
            if (args.Length == 4 && args[0] == "export")
            {
                if (!(args[3].EndsWith('Z') || args[3].EndsWith("+00:00", StringComparison.Ordinal)) ||
                    !DateTimeOffset.TryParseExact(args[3],
                        ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                         "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var asOf) ||
                    !ResearchDataset.Utc(asOf)) throw new ArgumentException("Explicit UTC cutoff required.");
                var archivePath = Path.GetFullPath(args[1]);
                var destination = Path.GetFullPath(args[2]);
                if (destination.StartsWith(Path.TrimEndingDirectorySeparator(archivePath) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Export destination must be outside archive.");
                var dataset = new ResearchBarArchive(archivePath).Export(asOf);
                var bytes = ResearchDatasetFile.Serialize(dataset);
                var audit = ResearchDatasetAudit.Inspect(bytes, new());
                ResearchDatasetFile.Save(destination, dataset);
                WriteAudit(output, audit);
                return 0;
            }
            if (args.Length == 4 && args[0] == "audit")
            {
                var bytes = File.ReadAllBytes(args[1]);
                var configuration = ReadConfiguration(args[2]);
                if (configuration is not null)
                {
                    if (configuration.FormatVersion != 1 || configuration.Plan is null ||
                        configuration.Policy is null || configuration.ScheduledClosures is null)
                        throw new ArgumentException("ConfigurationInvalid");
                    configuration.Policy.Validate();
                }
                // Audit can use a not-yet-calibrated template; plan and closures are still validated.
                var audit = ResearchDatasetAudit.Inspect(bytes, configuration?.Plan ?? new(),
                    configuration?.ScheduledClosures, configuration?.Policy.MinimumFolds ?? 3);
                ResearchDatasetFile.WriteNew(args[3], JsonSerializer.SerializeToUtf8Bytes(audit, ReportOptions));
                WriteAudit(output, audit);
                return audit.ReadyForResearch ? 0 : 2;
            }
            if (args.Length == 4 && args[0] == "run")
            {
                var report = ResearchWorkflow.Run(File.ReadAllBytes(args[1]), ReadConfiguration(args[2]));
                ResearchDatasetFile.WriteNew(args[3], ResearchWorkflow.Serialize(report));
                WriteSummary(output, report);
                return report.Passed ? 0 : 2;
            }
            output.WriteLine("Offline research only. Commands (paths with spaces must be quoted):\n" +
                "  init <configuration.json>\n" +
                "  export <archive-directory> <dataset.json> <AsOfUtc>\n" +
                "  audit <dataset.json> <configuration.json|-> <audit.json>\n" +
                "  run <dataset.json> <configuration.json|-> <report.json>\n" +
                "Outputs are atomic and never overwritten. '-' means no configuration. Exit: 0 success, 2 failed research/audit, 1 invalid input or I/O.");
            return args.Length == 0 ? 0 : 1;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or OverflowException)
        {
            // Do not echo paths, payloads or exception text that may contain private input.
            var category = e switch
            {
                JsonException => "SchemaInvalid: unknown, duplicate, missing or malformed JSON fields.",
                IOException or UnauthorizedAccessException => "StorageFailure: input unavailable or output cannot be created without overwrite.",
                _ when args.FirstOrDefault() == "export" => "ArchiveDataInvalid: check UTC cutoff, destination, each candle, single broker/source, and duplicate/conflict status.",
                _ => "ConfigurationOrDataInvalid: calibration required; verify all required values, plan and dataset."
            };
            output.WriteLine("Failed closed: " + category + " No validation pass established.");
            return 1;
        }
    }

    private static readonly JsonSerializerOptions ReportOptions = new(ResearchConfigurationFile.Options) { IgnoreReadOnlyProperties = false };
    private static ResearchConfiguration? ReadConfiguration(string path) => path == "-" ? null :
        ResearchConfigurationFile.Load(File.ReadAllBytes(path));

    private static void WriteAudit(TextWriter output, DatasetAuditReport audit)
    {
        output.WriteLine($"SHA-256: {audit.DatasetFingerprint ?? audit.InputSha256}; broker: {audit.BrokerSymbol}; source: {audit.Source}; AsOfUtc: {audit.AsOfUtc:O}");
        foreach (var tf in audit.Timeframes)
            output.WriteLine($"M{tf.TimeframeMinutes}: {tf.Count}; first open: {tf.FirstOpenUtc:O}; last close: {tf.LastCloseUtc:O}");
        output.WriteLine($"Planned folds: {audit.FoldCount}; causal warm-up: {audit.SufficientWarmup}");
        output.WriteLine("Hard invalid: " + string.Join(", ", audit.HardInvalid));
        output.WriteLine("Review required: " + string.Join(", ", audit.ReviewRequired));
        output.WriteLine("Insufficient evidence: " + string.Join(", ", audit.InsufficientEvidence));
    }

    private static void WriteSummary(TextWriter output, ResearchWorkflowReport report)
    {
        WriteAudit(output, report.Audit);
        var result = report.Validation;
        var gates = result.Gates;
        var metrics = result.Aggregate;
        output.WriteLine($"Passed: {report.Passed}; DataIntegrity: {gates.DataIntegrity}; LeakageAndReproducibility: {gates.LeakageAndReproducibility}; RiskLimits: {gates.RiskLimits}; StrategyPerformance: {gates.StrategyPerformance}");
        output.WriteLine($"Fold count: {result.Folds.Count}; total out-of-sample trades: {metrics?.TradeCount ?? 0}");
        string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NotEvaluated";
        output.WriteLine($"Aggregate net P/L: {Number(metrics?.NetProfit)}; return %: {Number(metrics?.ReturnPercent)}; max drawdown %: {Number(metrics?.MaximumDrawdownPercent)}; profit factor: {(metrics?.ProfitFactorUnbounded == true ? "Unbounded" : Number(metrics?.ProfitFactor))}; expectancy: {Number(metrics?.Expectancy)}");
        output.WriteLine("Baseline comparison: " + (result.Folds.Count == 0 ? "NotEvaluated" :
            string.Join(", ", result.Folds.Select((f, i) => $"fold {i + 1} strategy={Number(f.OutOfSample.Metrics.NetProfit)} reference={Number(f.Baseline.Metrics.NetProfit)} exceeded={f.OutOfSample.Metrics.NetProfit > f.Baseline.Metrics.NetProfit}"))));
        output.WriteLine("Cost-stress result (validation): " + (result.Folds.Count == 0 ? "NotEvaluated" :
            string.Join(", ", result.Folds.Select(f => $"return %={Number(f.HigherCosts.Metrics.ReturnPercent)} positive={f.HigherCosts.Metrics.NetProfit > 0}"))));
        output.WriteLine("Parameter-sensitivity result (validation): " + (result.Folds.Count == 0 ? "NotEvaluated" :
            string.Join(", ", result.Folds.Select(f => $"return %={Number(f.ParameterSensitivity.Metrics.ReturnPercent)} positive={f.ParameterSensitivity.Metrics.NetProfit > 0}"))));
        output.WriteLine("All gate failure reasons: " + string.Join(", ", gates.DataFailures.Concat(gates.MethodFailures).Concat(gates.RiskFailures).Concat(gates.PerformanceFailures)));
        output.WriteLine("Research-policy results only. A passing run does not prove a durable edge or authorize execution.");
    }
}
