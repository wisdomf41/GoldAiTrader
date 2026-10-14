using System.Text.RegularExpressions;

namespace GoldAiTrader.Core;

public enum ResearchSource { Mt5Telemetry, HistoricalImport, SyntheticFixture }

public sealed record ResearchBar(DateTimeOffset OpenTimeUtc, DateTimeOffset CloseTimeUtc,
    DateTimeOffset AvailableAtUtc, int TimeframeMinutes, decimal Open, decimal High, decimal Low,
    decimal Close, decimal? Volume, decimal? Spread)
{
    public MarketBar ToMarketBar() => new(OpenTimeUtc, Open, High, Low, Close,
        Close, Close + (Spread ?? 0), Volume ?? 0);
}

public sealed record ResearchDataset(int FormatVersion, string CanonicalSymbol, string BrokerSymbol,
    ResearchSource Source, DateTimeOffset AsOfUtc, IReadOnlyList<ResearchBar> Bars)
{
    public void Validate()
    {
        if (FormatVersion != 1 || CanonicalSymbol != "XAUUSD" ||
            string.IsNullOrEmpty(BrokerSymbol) || !Regex.IsMatch(BrokerSymbol, @"\A(?:XAUUSD|GOLD)[A-Za-z._-]{0,12}\z") ||
            !Enum.IsDefined(Source) || !Utc(AsOfUtc) || Bars is null || Bars.Count == 0)
            throw new ArgumentException("Invalid research dataset header.");
        var previous = new Dictionary<int, ResearchBar>();
        DateTimeOffset? lastAvailable = null;
        foreach (var bar in Bars)
        {
            if (bar is null || !Utc(bar.OpenTimeUtc) || !Utc(bar.CloseTimeUtc) || !Utc(bar.AvailableAtUtc) ||
                bar.TimeframeMinutes is not (5 or 15) ||
                bar.CloseTimeUtc - bar.OpenTimeUtc != TimeSpan.FromMinutes(bar.TimeframeMinutes) ||
                bar.OpenTimeUtc.Ticks % TimeSpan.FromMinutes(bar.TimeframeMinutes).Ticks != 0 ||
                bar.AvailableAtUtc < bar.CloseTimeUtc || bar.AvailableAtUtc > AsOfUtc ||
                bar.Volume < 0 || bar.Spread < 0 || lastAvailable > bar.AvailableAtUtc)
                throw new ArgumentException("Invalid, future or unordered completed research bar.");
            bar.ToMarketBar().Validate();
            if (previous.TryGetValue(bar.TimeframeMinutes, out var prior) && bar.OpenTimeUtc < prior.CloseTimeUtc)
                throw new ArgumentException("Duplicate, conflicting or overlapping research bar.");
            previous[bar.TimeframeMinutes] = bar;
            lastAvailable = bar.AvailableAtUtc;
        }
    }

    public static bool Utc(DateTimeOffset time) => time != default && time.Offset == TimeSpan.Zero;
}
