using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldAiTrader.Core;

namespace GoldAiTrader.Data;

public static class ResearchDatasetFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    public static byte[] Serialize(ResearchDataset dataset)
    {
        dataset.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(dataset, Options);
    }

    public static ResearchDataset Load(ReadOnlySpan<byte> bytes)
    {
        var dataset = JsonSerializer.Deserialize<ResearchDataset>(bytes, Options)
            ?? throw new ArgumentException("Missing research dataset.");
        dataset.Validate();
        return dataset;
    }

    public static string Fingerprint(ResearchDataset dataset) =>
        Convert.ToHexString(SHA256.HashData(Serialize(dataset)));

    public static void Save(string path, ResearchDataset dataset)
    {
        var bytes = Serialize(dataset);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>One immutable file per accepted candle; retries cannot overwrite captured observations.</summary>
public sealed class ResearchBarArchive(string directory)
{
    private readonly object sync = new();

    public void Capture(ResearchDataset singleBar)
    {
        singleBar.Validate();
        if (singleBar.Source != ResearchSource.Mt5Telemetry || singleBar.Bars.Count != 1)
            throw new ArgumentException("Capture requires one accepted telemetry candle.");
        var bar = singleBar.Bars[0];
        var key = FormattableString.Invariant($"{singleBar.BrokerSymbol}|{bar.TimeframeMinutes}|{bar.OpenTimeUtc.Ticks}");
        var name = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".json";
        lock (sync)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                var existing = ResearchDatasetFile.Load(File.ReadAllBytes(path));
                if (existing.Source != ResearchSource.Mt5Telemetry || existing.Bars.Count != 1 ||
                    existing.BrokerSymbol != singleBar.BrokerSymbol ||
                    (existing.Bars[0] with { AvailableAtUtc = bar.AvailableAtUtc }) != bar)
                    throw new InvalidOperationException("Conflicting captured candle.");
                return;
            }
            ResearchDatasetFile.Save(path, singleBar);
        }
    }

    public ResearchDataset Export(DateTimeOffset asOfUtc)
    {
        lock (sync)
        {
            var files = Directory.EnumerateFiles(directory, "*.json")
                .Select(path => ResearchDatasetFile.Load(File.ReadAllBytes(path))).ToArray();
            if (files.Length == 0 || files.Any(x => x.BrokerSymbol != files[0].BrokerSymbol ||
                    x.Source != ResearchSource.Mt5Telemetry || x.Bars.Count != 1))
                throw new ArgumentException("Archive must contain a single broker symbol and telemetry source.");
            var result = new ResearchDataset(1, "XAUUSD", files[0].BrokerSymbol,
                ResearchSource.Mt5Telemetry, asOfUtc, files.SelectMany(x => x.Bars)
                    .OrderBy(x => x.AvailableAtUtc).ThenBy(x => x.TimeframeMinutes).ToArray());
            result.Validate();
            return result;
        }
    }
}
