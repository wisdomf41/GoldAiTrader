using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Core;
using GoldAiTrader.Data;

namespace GoldAiTrader.MT5.BridgeHost;

public sealed class MT5ResearchCapture(MT5BridgeHostOptions options, TimeProvider clock,
    ILogger<MT5ResearchCapture> logger)
{
    private readonly ResearchBarArchive? archive = options.ResearchCaptureDirectory is { } path
        ? new(path) : null;
    private int failed;

    public void CaptureAccepted(MT5CompletedBarMessage message)
    {
        if (archive is null) return;
        try
        {
            var bar = message.Bar;
            if (bar.CanonicalSymbol != "XAUUSD" ||
                (bar.Timeframe != TimeSpan.FromMinutes(5) && bar.Timeframe != TimeSpan.FromMinutes(15))) return;
            var now = clock.GetUtcNow();
            archive.Capture(new(1, bar.CanonicalSymbol, bar.BrokerSymbol, ResearchSource.Mt5Telemetry, now,
                [new(bar.OpenTimeUtc, bar.OpenTimeUtc + bar.Timeframe, now, (int)bar.Timeframe.TotalMinutes,
                    bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.Ask - bar.Bid)]));
            if (Interlocked.Exchange(ref failed, 0) != 0)
                logger.LogInformation("Research capture recovered; check the archive for missing candles.");
        }
        catch (Exception ex) when (IsNonFatal(ex))
        {
            if (Interlocked.Exchange(ref failed, 1) == 0)
            {
                try
                {
                    logger.LogError("Research capture failed; telemetry health is independent. Check archive storage and dataset validity.");
                }
                catch (Exception loggingFailure) when (IsNonFatal(loggingFailure))
                {
                    // A failing logging provider must not turn an optional capture failure into an ingestion failure.
                }
            }
        }
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
