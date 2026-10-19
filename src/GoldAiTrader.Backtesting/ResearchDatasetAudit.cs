using System.Security.Cryptography;
using System.Text.Json;
using GoldAiTrader.Core;
using GoldAiTrader.Data;

namespace GoldAiTrader.Backtesting;

public sealed record ResearchPlan(int TrainingBars = 1200, int ValidationBars = 600, int OutOfSampleBars = 600)
{
    public void Validate()
    {
        if (TrainingBars < 600 || ValidationBars < 1 || OutOfSampleBars < 1)
            throw new ArgumentException("Invalid research plan: at least 600 training M5 bars are required.");
        _ = checked(TrainingBars + ValidationBars + OutOfSampleBars);
    }
}

public sealed record TimeframeAudit(int TimeframeMinutes, int Count, DateTimeOffset? FirstOpenUtc,
    DateTimeOffset? LastCloseUtc, decimal ElapsedHours, decimal ObservedHours);
public sealed record ResearchGap(int TimeframeMinutes, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    bool ScheduledClosure);
public sealed record DatasetAuditReport(string InputSha256, string? DatasetFingerprint, int? FormatVersion,
    string? CanonicalSymbol, string? BrokerSymbol, ResearchSource? Source, DateTimeOffset? AsOfUtc,
    IReadOnlyList<TimeframeAudit> Timeframes, IReadOnlyDictionary<string, bool> Checks,
    IReadOnlyList<ResearchGap> Gaps, ResearchPlan Plan, int FoldCount, bool SufficientWarmup,
    IReadOnlyList<string> HardInvalid, IReadOnlyList<string> ReviewRequired,
    IReadOnlyList<string> InsufficientEvidence)
{
    public bool ReadyForResearch => HardInvalid.Count == 0 && ReviewRequired.Count == 0 && InsufficientEvidence.Count == 0;
}

public static class ResearchDatasetAudit
{
    public static DatasetAuditReport Inspect(ReadOnlySpan<byte> bytes, ResearchPlan plan,
        IReadOnlyList<ResearchClosure>? scheduledClosures = null, int minimumFolds = 3)
    {
        plan.Validate();
        if (minimumFolds < 2) throw new ArgumentException("At least two folds are required.");
        var closures = scheduledClosures ?? [];
        DateTimeOffset? priorEnd = null;
        foreach (var c in closures)
        {
            if (c is null || !ResearchDataset.Utc(c.StartUtc) || !ResearchDataset.Utc(c.EndUtc) ||
                c.StartUtc >= c.EndUtc || priorEnd > c.StartUtc ||
                c.StartUtc.Ticks % TimeSpan.FromMinutes(15).Ticks != 0 ||
                c.EndUtc.Ticks % TimeSpan.FromMinutes(15).Ticks != 0)
                throw new ArgumentException("Invalid scheduled closures.");
            priorEnd = c.EndUtc;
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        ResearchDataset data;
        try { data = ResearchDatasetFile.ParseForAudit(bytes); }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return new(hash, null, null, null, null, null, null, [],
                new SortedDictionary<string, bool> { ["Schema"] = false }, [], plan, 0, false,
                ["SchemaInvalid"], [], ["NotEvaluated"]);
        }
        var checks = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        var hard = new List<string>();
        void Check(string name, bool pass) { checks[name] = pass; if (!pass) hard.Add(name); }
        var bars = data.Bars?.Where(b => b is not null).ToArray() ?? [];
        var symbolSafe = data.BrokerSymbol is not null && System.Text.RegularExpressions.Regex.IsMatch(
            data.BrokerSymbol, @"\A(?:XAUUSD|GOLD)[A-Za-z._-]{0,12}\z");
        Check("Header", data.FormatVersion == 1 && data.CanonicalSymbol == "XAUUSD" && symbolSafe &&
            Enum.IsDefined(data.Source) && data.Bars is not null && bars.Length == data.Bars.Count && bars.Length > 0);
        Check("Utc", ResearchDataset.Utc(data.AsOfUtc) && bars.All(b => ResearchDataset.Utc(b.OpenTimeUtc) &&
            ResearchDataset.Utc(b.CloseTimeUtc) && ResearchDataset.Utc(b.AvailableAtUtc)));
        Check("TimeframeAlignment", bars.All(b => b.TimeframeMinutes is 5 or 15 &&
            b.CloseTimeUtc - b.OpenTimeUtc == TimeSpan.FromMinutes(b.TimeframeMinutes) &&
            b.OpenTimeUtc.Ticks % TimeSpan.FromMinutes(b.TimeframeMinutes).Ticks == 0));
        Check("Ohlc", bars.All(b => b.Open > 0 && b.Close > 0 && b.Low > 0 &&
            b.High >= b.Low && b.Open >= b.Low && b.Open <= b.High && b.Close >= b.Low && b.Close <= b.High));
        Check("NonnegativeSpreadAndVolume", bars.All(b => !(b.Spread < 0 || b.Volume < 0)));
        Check("CausalAvailability", bars.All(b => b.AvailableAtUtc >= b.CloseTimeUtc && b.AvailableAtUtc <= data.AsOfUtc));
        Check("AvailabilityOrder", bars.Zip(bars.Skip(1)).All(pair => pair.First.AvailableAtUtc <= pair.Second.AvailableAtUtc));
        Check("DuplicateConflictOrOverlap", bars.GroupBy(b => b.TimeframeMinutes).All(g =>
            g.Zip(g.Skip(1)).All(pair => pair.First.CloseTimeUtc <= pair.Second.OpenTimeUtc)));
        Check("ScheduledClosureConsistency", bars.All(b => !closures.Any(c => b.OpenTimeUtc < c.EndUtc && b.CloseTimeUtc > c.StartUtc)));
        try { data.Validate(); }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        {
            if (hard.Count == 0) hard.Add("DatasetInvalid");
        }
        var streams = new List<TimeframeAudit>();
        var gaps = new List<ResearchGap>();
        foreach (var tf in new[] { 5, 15 })
        {
            var stream = bars.Where(b => b.TimeframeMinutes == tf).OrderBy(b => b.OpenTimeUtc).ToArray();
            streams.Add(new(tf, stream.Length, stream.FirstOrDefault()?.OpenTimeUtc, stream.LastOrDefault()?.CloseTimeUtc,
                stream.Length == 0 ? 0 : (decimal)(stream[^1].CloseTimeUtc - stream[0].OpenTimeUtc).TotalHours,
                stream.Length * tf / 60m));
            foreach (var (previous, next) in stream.Zip(stream.Skip(1)))
                if (next.OpenTimeUtc > previous.CloseTimeUtc)
                    gaps.Add(new(tf, previous.CloseTimeUtc, next.OpenTimeUtc,
                        closures.Any(c => c.StartUtc == previous.CloseTimeUtc && c.EndUtc == next.OpenTimeUtc)));
        }
        var evidence = new List<string>();
        var folds = hard.Count == 0 ? WalkForwardValidation.Plan(data, plan.TrainingBars, plan.ValidationBars, plan.OutOfSampleBars) : [];
        var warm = false;
        if (hard.Count == 0)
        {
            // Availability, not just counts, determines usable warmup. Every fold starts fresh.
            var execution = bars.Where(b => b.TimeframeMinutes == 5).ToArray();
            var context = bars.Where(b => b.TimeframeMinutes == 15).ToArray();
            warm = execution.Length > 50 && context.Length >= 200 &&
                execution.Any(b => b.OpenTimeUtc >= context[199].AvailableAtUtc && b.OpenTimeUtc >= execution[49].AvailableAtUtc);
            foreach (var fold in folds)
            {
                var input = data with { Bars = bars.Where(b => b.OpenTimeUtc >= fold.Training.StartUtc &&
                    b.CloseTimeUtc <= fold.Validation.StartUtc).ToArray() };
                warm &= DatasetBacktest.FeaturesAt(input, fold.Validation.StartUtc, new(0, 0, 0)) is not null;
            }
        }
        if (!warm) evidence.Add("InsufficientCausalWarmup");
        if (folds.Count == 0) evidence.Add("NoWalkForwardFolds");
        if (folds.Count < minimumFolds) evidence.Add("InsufficientFoldCount");
        return new(hash, hard.Count == 0 ? ResearchDatasetFile.Fingerprint(data) : null, data.FormatVersion,
            data.CanonicalSymbol == "XAUUSD" ? data.CanonicalSymbol : null, symbolSafe ? data.BrokerSymbol : null,
            Enum.IsDefined(data.Source) ? data.Source : null, data.AsOfUtc, streams, checks, gaps, plan, folds.Count, warm,
            hard, gaps.Any(g => !g.ScheduledClosure) ? ["CalendarGapRequiresReview"] : [], evidence);
    }
}
