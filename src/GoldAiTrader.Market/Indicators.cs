using GoldAiTrader.Core;

namespace GoldAiTrader.Market;

public static class Indicators
{
    public static decimal Ema(IReadOnlyList<decimal> values, int period)
    {
        Validate(values, period);
        var multiplier = 2m / (period + 1m);
        var ema = values.Take(period).Average();
        for (var i = period; i < values.Count; i++) ema = values[i] * multiplier + ema * (1m - multiplier);
        return ema;
    }

    public static decimal Atr(IReadOnlyList<MarketBar> bars, int period)
    {
        if (bars.Count < period + 1 || period < 1) throw new ArgumentException("Insufficient bars for ATR.");
        ValidateChronology(bars);
        decimal total = 0;
        for (var i = bars.Count - period; i < bars.Count; i++)
        {
            var previous = bars[i - 1].Close;
            total += Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - previous), Math.Abs(bars[i].Low - previous)));
        }
        return total / period;
    }

    public static decimal Rsi(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count < period + 1 || period < 1) throw new ArgumentException("Insufficient values for RSI.");
        decimal gains = 0, losses = 0;
        for (var i = values.Count - period; i < values.Count; i++)
        {
            var change = values[i] - values[i - 1];
            if (change > 0) gains += change; else losses -= change;
        }
        if (losses == 0) return gains == 0 ? 50m : 100m;
        return 100m - 100m / (1m + gains / losses);
    }

    public static decimal Adx(IReadOnlyList<MarketBar> bars, int period)
    {
        if (bars.Count < period + 1 || period < 1) throw new ArgumentException("Insufficient bars for ADX.");
        ValidateChronology(bars);
        decimal plus = 0, minus = 0, trueRange = 0;
        for (var i = bars.Count - period; i < bars.Count; i++)
        {
            var up = bars[i].High - bars[i - 1].High;
            var down = bars[i - 1].Low - bars[i].Low;
            if (up > down && up > 0) plus += up;
            if (down > up && down > 0) minus += down;
            trueRange += Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close), Math.Abs(bars[i].Low - bars[i - 1].Close)));
        }
        if (trueRange == 0) return 0;
        var pdi = 100m * plus / trueRange;
        var mdi = 100m * minus / trueRange;
        return pdi + mdi == 0 ? 0 : 100m * Math.Abs(pdi - mdi) / (pdi + mdi);
    }

    public static void ValidateChronology(IReadOnlyList<MarketBar> bars)
    {
        for (var i = 0; i < bars.Count; i++)
        {
            bars[i].Validate();
            if (i > 0 && bars[i].OpenTime <= bars[i - 1].OpenTime)
                throw new ArgumentException("Bars must be strictly chronological.");
        }
    }

    private static void Validate(IReadOnlyList<decimal> values, int period)
    {
        if (period < 1 || values.Count < period || values.Any(value => value <= 0))
            throw new ArgumentException("Invalid indicator input.");
    }
}

public static class SessionClassifier
{
    public static MarketSession Classify(DateTimeOffset timestamp)
    {
        var hour = timestamp.ToUniversalTime().Hour;
        if (hour is >= 13 and < 16) return MarketSession.LondonNewYorkOverlap;
        if (hour is >= 7 and < 13) return MarketSession.London;
        if (hour is >= 16 and < 21) return MarketSession.NewYork;
        return MarketSession.Closed;
    }
}
