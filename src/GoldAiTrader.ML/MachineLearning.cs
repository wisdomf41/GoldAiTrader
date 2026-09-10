using Microsoft.ML;
using Microsoft.ML.Data;

namespace GoldAiTrader.ML;

public enum MlMode { Disabled, Shadow, Filter }
public sealed record MlOptions(MlMode Mode = MlMode.Disabled, float ApprovalThreshold = 0.60f, string FeatureSchemaVersion = "tpv1-1");
public sealed record TradeFeatureRow(DateTimeOffset Time, float Atr, float Rsi, float Adx, float Spread,
    float Volatility, float TrendDistance, bool Label, string DatasetVersion);
public sealed record TradeQualityPrediction(float Probability, bool Approved, string ModelVersion);

public sealed class MlTradeQualityFilter
{
    private readonly MLContext context = new(seed: 1729);
    private ITransformer? model;
    private string modelVersion = "untrained";

    public void Train(IReadOnlyList<TradeFeatureRow> rows, DateTimeOffset trainingCutoff, string featureSchemaVersion)
    {
        if (rows.Count < 10) throw new ArgumentException("At least ten chronological observations are required.");
        if (rows.Any(row => row.Time > trainingCutoff)) throw new InvalidOperationException("Future observation leaked into training.");
        if (rows.Zip(rows.Skip(1)).Any(pair => pair.First.Time >= pair.Second.Time))
            throw new InvalidOperationException("Training observations must be strictly chronological.");
        var data = context.Data.LoadFromEnumerable(rows.Select(FeatureInput.From));
        var pipeline = context.Transforms.Concatenate("Features", nameof(FeatureInput.Atr), nameof(FeatureInput.Rsi),
                nameof(FeatureInput.Adx), nameof(FeatureInput.Spread), nameof(FeatureInput.Volatility), nameof(FeatureInput.TrendDistance))
            .Append(context.BinaryClassification.Trainers.SdcaLogisticRegression());
        model = pipeline.Fit(data);
        modelVersion = $"{featureSchemaVersion}-{trainingCutoff:yyyyMMddHHmm}";
    }

    public TradeQualityPrediction Predict(TradeFeatureRow row, MlOptions options)
    {
        if (options.Mode == MlMode.Disabled) return new(1f, true, "disabled");
        if (model is null) return new(0f, false, "unavailable");
        var engine = context.Model.CreatePredictionEngine<FeatureInput, PredictionOutput>(model);
        var output = engine.Predict(FeatureInput.From(row));
        var approved = options.Mode == MlMode.Shadow || output.Probability >= options.ApprovalThreshold;
        return new(output.Probability, approved, modelVersion);
    }

    private sealed class FeatureInput
    {
        public float Atr { get; set; } public float Rsi { get; set; } public float Adx { get; set; }
        public float Spread { get; set; } public float Volatility { get; set; } public float TrendDistance { get; set; }
        public bool Label { get; set; }
        public static FeatureInput From(TradeFeatureRow row) => new() { Atr = row.Atr, Rsi = row.Rsi, Adx = row.Adx,
            Spread = row.Spread, Volatility = row.Volatility, TrendDistance = row.TrendDistance, Label = row.Label };
    }
    private sealed class PredictionOutput { [ColumnName("Probability")] public float Probability { get; set; } }
}

public sealed record WalkForwardFold(DateTimeOffset TrainingThrough, DateTimeOffset TestFrom, DateTimeOffset TestThrough, int TrainingCount, int TestCount);
public static class WalkForwardEvaluator
{
    public static IReadOnlyList<WalkForwardFold> CreateFolds(IReadOnlyList<TradeFeatureRow> rows, int minimumTrainingSize, int testSize)
    {
        if (minimumTrainingSize < 10 || testSize < 1) throw new ArgumentException("Invalid walk-forward sizes.");
        if (rows.Zip(rows.Skip(1)).Any(pair => pair.First.Time >= pair.Second.Time))
            throw new InvalidOperationException("Rows must be chronological.");
        var folds = new List<WalkForwardFold>();
        for (var start = minimumTrainingSize; start + testSize <= rows.Count; start += testSize)
            folds.Add(new(rows[start - 1].Time, rows[start].Time, rows[start + testSize - 1].Time, start, testSize));
        return folds;
    }
}
