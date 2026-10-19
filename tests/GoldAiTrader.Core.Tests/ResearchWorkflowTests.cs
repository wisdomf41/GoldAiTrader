using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using GoldAiTrader.Backtesting;
using GoldAiTrader.Core;
using GoldAiTrader.Data;
using GoldAiTrader.Research;

namespace GoldAiTrader.Core.Tests;

public sealed class ResearchWorkflowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "research-workflow-" + Guid.NewGuid().ToString("N"));
    public ResearchWorkflowTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string PathFor(string name) => Path.Combine(directory, name);
    private static ResearchDataset Fixture(int count = 1320) => ResearchValidationTests.Dataset(count) with { Source = ResearchSource.SyntheticFixture };
    private static ResearchConfiguration Config() => new()
    {
        BrokerSymbol = "XAUUSD.demo", InitialCapital = 10000,
        Calibration = new(.2m, 3m, .02m, 1m, .01m, 1m, 100m, .01m, 100m, .01m, .1m),
        Plan = new(600, 30, 30), Policy = new(MinimumFolds: 2)
    };
    private static byte[] Raw(ResearchDataset dataset) => JsonSerializer.SerializeToUtf8Bytes(dataset);
    private static DatasetAuditReport Audit(ResearchDataset dataset) => ResearchDatasetAudit.Inspect(Raw(dataset), new(600, 30, 30), minimumFolds: 2);
    private string Capture(int count = 6)
    {
        var path = PathFor("archive");
        var archive = new ResearchBarArchive(path);
        var dataset = Fixture(count) with { Source = ResearchSource.Mt5Telemetry };
        foreach (var bar in dataset.Bars.Reverse()) archive.Capture(dataset with { Bars = [bar] });
        return path;
    }

    [Fact] public void ArchiveExportPreservesStreamsAndDeterministicFingerprint()
    {
        var archive = new ResearchBarArchive(Capture());
        var first = archive.Export(Fixture(6).AsOfUtc);
        var second = archive.Export(Fixture(6).AsOfUtc);
        Assert.Equal(6, first.Bars.Count(b => b.TimeframeMinutes == 5));
        Assert.Equal(2, first.Bars.Count(b => b.TimeframeMinutes == 15));
        Assert.Equal(ResearchSource.Mt5Telemetry, first.Source);
        Assert.Equal(ResearchDatasetFile.Fingerprint(first), ResearchDatasetFile.Fingerprint(second));
        Assert.Equal(ResearchDatasetFile.Serialize(first), ResearchDatasetFile.Serialize(second));
    }

    [Theory]
    [InlineData("broker")]
    [InlineData("duplicate")]
    [InlineData("conflict")]
    [InlineData("source")]
    [InlineData("unknown-source")]
    [InlineData("malformed")]
    [InlineData("multiple-bars")]
    public void ExportRejectsInvalidArchive(string problem)
    {
        var archive = Capture();
        var source = Directory.GetFiles(archive).First();
        var data = ResearchDatasetFile.Load(File.ReadAllBytes(source));
        data = problem switch
        {
            "broker" => data with { BrokerSymbol = "GOLD.demo" },
            "source" => data with { Source = ResearchSource.HistoricalImport },
            "unknown-source" => data with { Source = (ResearchSource)99 },
            "conflict" => data with { Bars = [data.Bars[0] with { Close = data.Bars[0].Close + .01m }] },
            "malformed" => data with { Bars = [data.Bars[0] with { Low = -1 }] },
            "multiple-bars" => Fixture(6) with { Source = ResearchSource.Mt5Telemetry },
            _ => data
        };
        File.WriteAllBytes(Path.Combine(archive, "extra.json"), Raw(data));
        Assert.ThrowsAny<ArgumentException>(() => new ResearchBarArchive(archive).Export(Fixture(6).AsOfUtc));
        Assert.False(File.Exists(PathFor("export.json")));
    }

    [Fact] public void ExportRequiresUtcAndCutoffAfterAllObservations()
    {
        var archive = new ResearchBarArchive(Capture());
        Assert.Throws<ArgumentException>(() => archive.Export(Fixture(6).AsOfUtc.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => archive.Export(Fixture(6).AsOfUtc.AddSeconds(-1)));
    }

    [Fact] public void AtomicPublicationDoesNotOverwriteAndCleansTemporaryFile()
    {
        var path = PathFor("dataset.json");
        var bytes = ResearchDatasetFile.Serialize(Fixture(6));
        ResearchDatasetFile.Save(path, Fixture(6));
        Assert.Throws<IOException>(() => ResearchDatasetFile.Save(path, Fixture(9)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Throws<ArgumentException>(() => ResearchDatasetFile.Save(PathFor("invalid.json"), Fixture(0)));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact] public void AuditReportsCountsDurationsFingerprintAndPlan()
    {
        var data = Fixture();
        var audit = Audit(data);
        Assert.Empty(audit.HardInvalid);
        Assert.Empty(audit.ReviewRequired);
        Assert.Empty(audit.InsufficientEvidence);
        Assert.True(audit.ReadyForResearch);
        Assert.Equal(ResearchDatasetFile.Fingerprint(data), audit.DatasetFingerprint);
        Assert.Equal(1, audit.FormatVersion);
        Assert.Equal("XAUUSD", audit.CanonicalSymbol);
        Assert.Equal("XAUUSD.demo", audit.BrokerSymbol);
        Assert.Equal(data.AsOfUtc, audit.AsOfUtc);
        Assert.Equal(2, audit.FoldCount);
        Assert.True(audit.SufficientWarmup);
        Assert.Equal(1320, audit.Timeframes[0].Count);
        Assert.Equal(440, audit.Timeframes[1].Count);
        Assert.Equal(110, audit.Timeframes[0].ElapsedHours);
        Assert.Equal(data.Bars[0].OpenTimeUtc, audit.Timeframes[0].FirstOpenUtc);
        Assert.Equal(data.AsOfUtc, audit.Timeframes[1].LastCloseUtc);
        Assert.All(audit.Checks.Values, Assert.True);
    }

    [Theory]
    [InlineData("utc", "Utc")]
    [InlineData("alignment", "TimeframeAlignment")]
    [InlineData("ohlc", "Ohlc")]
    [InlineData("spread", "NonnegativeSpreadAndVolume")]
    [InlineData("volume", "NonnegativeSpreadAndVolume")]
    [InlineData("future", "CausalAvailability")]
    [InlineData("early", "CausalAvailability")]
    [InlineData("duplicate", "DuplicateConflictOrOverlap")]
    [InlineData("conflict", "DuplicateConflictOrOverlap")]
    public void AuditCategorizesInvalidCandles(string error, string category)
    {
        var data = Fixture(6);
        var bars = data.Bars.ToArray();
        bars[0] = error switch
        {
            "utc" => bars[0] with { OpenTimeUtc = bars[0].OpenTimeUtc.ToOffset(TimeSpan.FromHours(1)) },
            "alignment" => bars[0] with { OpenTimeUtc = bars[0].OpenTimeUtc.AddSeconds(1) },
            "ohlc" => bars[0] with { High = 1 },
            "spread" => bars[0] with { Spread = -1 },
            "volume" => bars[0] with { Volume = -1 },
            "future" => bars[0] with { AvailableAtUtc = data.AsOfUtc.AddDays(1) },
            "early" => bars[0] with { AvailableAtUtc = bars[0].OpenTimeUtc },
            _ => bars[0]
        };
        if (error is "duplicate" or "conflict") bars[1] = bars[0] with { Close = bars[0].Close + (error == "conflict" ? .01m : 0) };
        var audit = Audit(data with { Bars = bars });
        Assert.Contains(category, audit.HardInvalid);
        Assert.False(audit.Checks[category]);
        Assert.Null(audit.DatasetFingerprint);
        Assert.False(audit.ReadyForResearch);
    }

    [Fact] public void SchemaErrorsDoNotEchoPrivateInput()
    {
        var audit = ResearchDatasetAudit.Inspect(Encoding.UTF8.GetBytes("{\"password\":\"private-test-value\"}"), new());
        Assert.Contains("SchemaInvalid", audit.HardInvalid);
        Assert.DoesNotContain("private-test-value", JsonSerializer.Serialize(audit));
        Assert.False(audit.ReadyForResearch);
    }

    [Fact] public void GapNeedsExplicitClosureAndIsNeverInferredFromWeekend()
    {
        var data = Fixture(2000);
        var start = data.Bars[0].OpenTimeUtc.AddHours(51);
        var closure = new ResearchClosure(start, start.AddHours(1));
        data = data with { Bars = data.Bars.Where(b => b.CloseTimeUtc <= closure.StartUtc || b.OpenTimeUtc >= closure.EndUtc).ToArray() };
        var audit = Audit(data);
        Assert.Empty(audit.HardInvalid);
        Assert.Equal(2, audit.Gaps.Count);
        Assert.Contains("CalendarGapRequiresReview", audit.ReviewRequired);
        var reviewed = ResearchDatasetAudit.Inspect(Raw(data), new(600, 30, 30), [closure], 2);
        Assert.Empty(reviewed.ReviewRequired);
        Assert.All(reviewed.Gaps, gap => Assert.True(gap.ScheduledClosure));
        var contradictory = ResearchDatasetAudit.Inspect(Raw(Fixture(2000)), new(), [closure]);
        Assert.Contains("ScheduledClosureConsistency", contradictory.HardInvalid);
    }

    [Fact] public void InsufficientWarmupAndFoldsAreEvidenceNotMalformedData()
    {
        var audit = Audit(Fixture(30));
        Assert.Empty(audit.HardInvalid);
        Assert.Contains("InsufficientCausalWarmup", audit.InsufficientEvidence);
        Assert.Contains("NoWalkForwardFolds", audit.InsufficientEvidence);
        Assert.Contains("InsufficientFoldCount", audit.InsufficientEvidence);
        Assert.False(audit.ReadyForResearch);
    }

    [Fact] public void WarmupCannotUseLateContextOrBorrowFromPreviousFold()
    {
        var data = Fixture();
        var fold = WalkForwardValidation.Plan(data, 600, 30, 30)[1];
        data = data with { Bars = data.Bars.Where(b => b.TimeframeMinutes != 15 || b.OpenTimeUtc != fold.Training.StartUtc).ToArray() };
        Assert.Contains("InsufficientCausalWarmup", Audit(data).InsufficientEvidence);
    }

    [Theory]
    [InlineData("spread")]
    [InlineData("commission")]
    [InlineData("slippage")]
    [InlineData("multiplier")]
    [InlineData("tick")]
    [InlineData("value")]
    [InlineData("contract")]
    [InlineData("minimum")]
    [InlineData("maximum")]
    [InlineData("step")]
    [InlineData("stop")]
    public void InvalidCalibrationIsRejected(string field)
    {
        var config = Config();
        var c = config.Calibration;
        c = field switch
        {
            "spread" => c with { FallbackSpread = -1 }, "commission" => c with { CommissionPerLotPerSide = -1 },
            "slippage" => c with { SlippagePricePerSide = -1 }, "multiplier" => c with { SpreadMultiplier = .5m },
            "tick" => c with { TickSize = 0 }, "value" => c with { TickValuePerLot = 0 },
            "contract" => c with { ContractSize = 10 }, "minimum" => c with { MinimumVolume = 0 },
            "maximum" => c with { MaximumVolume = .001m }, "step" => c with { VolumeStep = 0 },
            _ => c with { MinimumStopDistance = -1 }
        };
        Assert.ThrowsAny<ArgumentException>(() => (config with { Calibration = c }).Validate("XAUUSD.demo"));
    }

    [Fact] public void EveryCalibrationValueMustBeProvided()
    {
        foreach (var property in typeof(BrokerCalibration).GetProperties())
        {
            var values = JsonSerializer.SerializeToNode(Config().Calibration)!.AsObject();
            values.Remove(property.Name);
            var calibration = JsonSerializer.Deserialize<BrokerCalibration>(values.ToJsonString(), ResearchConfigurationFile.Options)!;
            Assert.Throws<ArgumentException>(() => (Config() with { Calibration = calibration }).Validate("XAUUSD.demo"));
        }
    }

    [Fact] public void ConfigurationRoundTripContainsOnlySafeSettingsAndRejectsPrivateFields()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Config(), ResearchConfigurationFile.Options);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var field in new[] { "AccountIdentifier", "BridgeInstanceId", "Password", "SharedSecret", "Token" })
        {
            Assert.DoesNotContain(field, text, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<JsonException>(() => ResearchConfigurationFile.Load(Encoding.UTF8.GetBytes(text.Insert(1, $"\"{field}\":\"private-test-value\","))));
        }
        var loaded = ResearchConfigurationFile.Load(bytes);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(loaded, ResearchConfigurationFile.Options));
        Assert.Equal(Config().Validate("XAUUSD.demo").Costs, loaded.Validate("XAUUSD.demo").Costs);
        Assert.Throws<ArgumentException>(() => loaded.Validate("GOLD.demo"));
    }

    [Fact] public void TemplateAndMissingCalibrationFailClosed()
    {
        ResearchConfigurationFile.SaveTemplate(PathFor("settings.json"));
        var template = ResearchConfigurationFile.Load(File.ReadAllBytes(PathFor("settings.json")));
        foreach (var config in new ResearchConfiguration?[] { null, template, Config() with { Calibration = new() } })
        {
            var report = ResearchWorkflow.Run(Raw(Fixture()), config);
            Assert.False(report.Passed);
            Assert.Contains("CalibrationRequired", report.Validation.Gates.MethodFailures);
            Assert.Empty(report.Validation.Folds);
        }
    }

    [Fact] public void RunnerIsDeterministicAndPreservesSyntheticEvidenceFailure()
    {
        var first = ResearchWorkflow.Run(Raw(Fixture()), Config());
        var second = ResearchWorkflow.Run(Raw(Fixture()), Config());
        Assert.Equal(ResearchWorkflow.Serialize(first), ResearchWorkflow.Serialize(second));
        Assert.Equal(2, first.Validation.Folds.Count);
        Assert.Contains("SyntheticEvidenceOnly", first.Validation.Gates.MethodFailures);
        Assert.False(first.Passed);
        Assert.NotNull(first.Settings);
        var text = Encoding.UTF8.GetString(ResearchWorkflow.Serialize(first));
        foreach (var field in new[] { "DataIntegrity", "LeakageAndReproducibility", "RiskLimits", "StrategyPerformance", "Calibration", "FallbackSpread", "Passed" })
            Assert.Contains(field, text);
    }

    [Theory]
    [InlineData(ResearchSource.Mt5Telemetry)]
    [InlineData(ResearchSource.HistoricalImport)]
    public void RealSourceLabelCannotBypassInsufficientEvidence(ResearchSource source)
    {
        var report = ResearchWorkflow.Run(Raw(Fixture(30) with { Source = source }), Config());
        Assert.False(report.Passed);
        Assert.Empty(report.Validation.Folds);
        Assert.Contains("NoWalkForwardFolds", report.Validation.Gates.MethodFailures);
    }

    [Fact] public void RunnerStopsOnUnreviewedGapEvenOutsideEvaluationWindows()
    {
        var data = Fixture();
        data = data with { Bars = data.Bars.Skip(1).ToArray() };
        var broken = data with { Bars = data.Bars.Where((_, i) => i != 20).ToArray() };
        var report = ResearchWorkflow.Run(Raw(broken), Config());
        Assert.Contains("CalendarGapRequiresReview", report.Validation.Gates.DataFailures);
        Assert.Empty(report.Validation.Folds);
    }

    [Fact] public void CommandsExportAuditAndRunWithNoOverwriteAndExplicitFailures()
    {
        var archive = Capture();
        var writer = new StringWriter();
        var dataset = PathFor("dataset.json");
        Assert.Equal(0, ResearchCommand.Execute(["export", archive, dataset, Fixture(6).AsOfUtc.ToString("O")], writer));
        Assert.Contains("Mt5Telemetry", writer.ToString());
        Assert.Contains("M15: 2", writer.ToString());
        Assert.Equal(1, ResearchCommand.Execute(["export", archive, dataset, Fixture(6).AsOfUtc.ToString("O")], writer));
        Assert.Equal(1, ResearchCommand.Execute(["export", archive, Path.Combine(archive, "export.json"), Fixture(6).AsOfUtc.ToString("O")], writer));
        Assert.Equal(2, ResearchCommand.Execute(["audit", dataset, "-", PathFor("audit.json")], writer));
        Assert.Equal(2, ResearchCommand.Execute(["run", dataset, "-", PathFor("report.json")], writer));
        Assert.Contains("CalibrationRequired", writer.ToString());
        Assert.Contains("Passed: False", writer.ToString());
        Assert.Equal(1, ResearchCommand.Execute(["run", dataset, "-", PathFor("report.json")], writer));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact] public async Task ConcurrentPublicationHasExactlyOneWinner()
    {
        var path = PathFor("concurrent.json");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { ResearchDatasetFile.Save(path, Fixture(6)); return true; }
            catch (IOException) { return false; }
        })));
        Assert.Single(attempts, value => value);
        Assert.Equal(8, ResearchDatasetFile.Load(File.ReadAllBytes(path)).Bars.Count);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact] public void GeneratedOutputConventionIsIgnored()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GoldAiTrader.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        Assert.Contains("research-output/", File.ReadAllLines(Path.Combine(root.FullName, ".gitignore")));
    }

    [Fact] public void CaptureConfigurationIsExplicitAndExecutionStaysDisabled()
    {
        var settings = GoldAiTrader.MT5.BridgeHost.MT5BridgeHostSettings.FromConfiguration(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["MT5Bridge:ResearchCaptureDirectory"] = directory }).Build());
        Assert.Equal(directory, settings.Host.ResearchCaptureDirectory);
        Assert.False(settings.Host.ExternalExecutionEnabled);
        var empty = GoldAiTrader.MT5.BridgeHost.MT5BridgeHostSettings.FromConfiguration(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Null(empty.Host.ResearchCaptureDirectory);
        Assert.False(empty.Host.ExternalExecutionEnabled);
        Assert.Throws<InvalidOperationException>(() =>
            (MT5BridgeHostTestFixture.ValidOptions() with { ResearchCaptureDirectory = "relative" }).Validate());
    }

    [Fact] public void MalformedAuditConfigurationFailsWithoutWritingReport()
    {
        File.WriteAllBytes(PathFor("dataset.json"), Raw(Fixture(6)));
        File.WriteAllText(PathFor("settings.json"), "{\"Policy\":null}");
        Assert.Equal(1, ResearchCommand.Execute(["audit", PathFor("dataset.json"), PathFor("settings.json"), PathFor("audit.json")], new StringWriter()));
        Assert.False(File.Exists(PathFor("audit.json")));
    }    [Theory]
    [InlineData("12:00Z")]
    [InlineData("2025-01-06Z")]
    [InlineData("2025-01-06T00:30:00")]
    [InlineData("2025-01-06T01:30:00+01:00")]
    public void ExportRejectsIncompleteOrNonUtcCutoff(string cutoff)
    {
        var writer = new StringWriter();
        Assert.Equal(1, ResearchCommand.Execute(["export", Capture(), PathFor("dataset.json"), cutoff], writer));
        Assert.False(File.Exists(PathFor("dataset.json")));
    }

    [Theory]
    [InlineData("2025-01-06T00:30:00Z")]
    [InlineData("2025-01-06T00:30:00.0000000+00:00")]
    public void ExportAcceptsCompleteUtcCutoff(string cutoff)
    {
        Assert.Equal(0, ResearchCommand.Execute(["export", Capture(), PathFor("dataset.json"), cutoff], new StringWriter()));
        Assert.Equal(Fixture(6).AsOfUtc, ResearchDatasetFile.Load(File.ReadAllBytes(PathFor("dataset.json"))).AsOfUtc);
    }}
