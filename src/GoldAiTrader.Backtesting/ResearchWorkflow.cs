using System.Text.Json;
using System.Text.Json.Serialization;
using GoldAiTrader.Core;
using GoldAiTrader.Data;
using GoldAiTrader.Risk;
using GoldAiTrader.Strategy;

namespace GoldAiTrader.Backtesting;

// No free-form metadata: calibration files contain only the research contract and assumptions.
public sealed record BrokerCalibration(decimal? FallbackSpread = null, decimal? CommissionPerLotPerSide = null,
    decimal? SlippagePricePerSide = null, decimal? SpreadMultiplier = null, decimal? TickSize = null,
    decimal? TickValuePerLot = null, decimal? ContractSize = null, decimal? MinimumVolume = null,
    decimal? MaximumVolume = null, decimal? VolumeStep = null, decimal? MinimumStopDistance = null)
{
    public ResearchRunSettings Settings(string brokerSymbol, decimal capital, TrendPullbackOptions strategy,
        RiskOptions risk, IReadOnlyList<ResearchClosure> closures)
    {
        if (FallbackSpread is null || CommissionPerLotPerSide is null || SlippagePricePerSide is null ||
            SpreadMultiplier is null || TickSize is null || TickValuePerLot is null || ContractSize is null ||
            MinimumVolume is null || MaximumVolume is null || VolumeStep is null || MinimumStopDistance is null)
            throw new ArgumentException("CalibrationRequired");
        var settings = new ResearchRunSettings(capital,
            new(TickSize.Value, TickValuePerLot.Value, MinimumVolume.Value, MaximumVolume.Value,
                VolumeStep.Value, MinimumStopDistance.Value, brokerSymbol, "XAUUSD", ContractSize.Value),
            new(FallbackSpread.Value, CommissionPerLotPerSide.Value, SlippagePricePerSide.Value, SpreadMultiplier.Value),
            strategy, risk, closures);
        settings.Validate();
        if (MinimumVolume.Value % VolumeStep.Value != 0 || MaximumVolume.Value % VolumeStep.Value != 0)
            throw new ArgumentException("CalibrationInvalid: volume limits must align with volume step.");
        return settings;
    }
}

public sealed record ResearchConfiguration
{
    public int FormatVersion { get; init; } = 1;
    public string? BrokerSymbol { get; init; }
    public decimal? InitialCapital { get; init; }
    public BrokerCalibration Calibration { get; init; } = new();
    public ResearchPlan Plan { get; init; } = new();
    public ValidationPolicy Policy { get; init; } = new();
    public TrendPullbackOptions Strategy { get; init; } = new();
    public RiskOptions Risk { get; init; } = new();
    public IReadOnlyList<ResearchClosure> ScheduledClosures { get; init; } = [];

    public ResearchRunSettings Validate(string datasetBrokerSymbol)
    {
        if (FormatVersion != 1 || Plan is null || Policy is null || Strategy is null || Risk is null || ScheduledClosures is null)
            throw new ArgumentException("ConfigurationInvalid");
        if (Calibration is null || InitialCapital is null || BrokerSymbol is null)
            throw new ArgumentException("CalibrationRequired");
        if (BrokerSymbol != datasetBrokerSymbol || !System.Text.RegularExpressions.Regex.IsMatch(
                BrokerSymbol, @"\A(?:XAUUSD|GOLD)[A-Za-z._-]{0,12}\z"))
            throw new ArgumentException("BrokerSymbolMismatch");
        if (ScheduledClosures.Any(c => c is null)) throw new ArgumentException("ConfigurationInvalid");
        Plan.Validate();
        Policy.Validate();
        return Calibration.Settings(BrokerSymbol, InitialCapital.Value, Strategy, Risk, ScheduledClosures);
    }
}

public static class ResearchConfigurationFile
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        IgnoreReadOnlyProperties = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static ResearchConfiguration Load(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<ResearchConfiguration>(bytes, Options) ?? throw new ArgumentException("CalibrationRequired");

    public static void SaveTemplate(string path) => ResearchDatasetFile.WriteNew(path,
        JsonSerializer.SerializeToUtf8Bytes(new ResearchConfiguration(), Options));
}

public sealed record ResearchWorkflowReport(int FormatVersion, DatasetAuditReport Audit,
    ResearchConfiguration? Configuration, ResearchRunSettings? Settings, WalkForwardReport Validation)
{
    public bool Passed => Validation.Gates.Passed;
}

public static class ResearchWorkflow
{
    public static ResearchWorkflowReport Run(byte[] datasetBytes, ResearchConfiguration? configuration)
    {
        // Inspect before calibration or simulation. Untrusted input strings are never copied to reports.
        var audit = ResearchDatasetAudit.Inspect(datasetBytes, new ResearchPlan());
        ResearchRunSettings? settings = null;
        string? configurationFailure = null;
        try
        {
            if (configuration is null) configurationFailure = "CalibrationRequired";
            else settings = configuration.Validate(audit.BrokerSymbol ?? "");
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        {
            configurationFailure = e.Message == "CalibrationRequired" ? "CalibrationRequired" : "ConfigurationInvalid";
        }
        if (settings is not null)
            audit = ResearchDatasetAudit.Inspect(datasetBytes, configuration!.Plan, configuration.ScheduledClosures, configuration.Policy.MinimumFolds);
        if (configurationFailure is not null || !audit.ReadyForResearch)
        {
            var method = audit.InsufficientEvidence.ToList();
            if (configurationFailure is not null) method.Add(configurationFailure);
            if (audit.Source == ResearchSource.SyntheticFixture) method.Add("SyntheticEvidenceOnly");
            var data = audit.HardInvalid.Concat(audit.ReviewRequired).ToArray();
            var failed = new WalkForwardReport(1, settings is not null ? configuration!.Policy : new(), [], null, null,
                new(data.Length == 0, false, false, false, data, method, ["NotEvaluated"], ["NotEvaluated"]));
            return new(1, audit, settings is null ? null : configuration, settings, failed);
        }
        var dataset = ResearchDatasetFile.Load(datasetBytes);
        var plan = configuration!.Plan;
        var folds = WalkForwardValidation.Plan(dataset, plan.TrainingBars, plan.ValidationBars, plan.OutOfSampleBars);
        var validation = WalkForwardValidation.Run(dataset, settings!, folds, configuration.Policy);
        return new(1, audit, configuration, settings, validation);
    }

    // Include computed gate properties in reports, unlike editable configuration templates.
    public static byte[] Serialize(ResearchWorkflowReport report) => JsonSerializer.SerializeToUtf8Bytes(report,
        new JsonSerializerOptions(ResearchConfigurationFile.Options) { IgnoreReadOnlyProperties = false });
}
