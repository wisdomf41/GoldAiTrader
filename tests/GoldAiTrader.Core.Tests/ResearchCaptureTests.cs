using System.Collections.Concurrent;
using GoldAiTrader.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Data;
using GoldAiTrader.MT5.BridgeHost;

namespace GoldAiTrader.Core.Tests;

public sealed class ResearchCaptureTests
{
    [Fact]
    public async Task OnlyAcceptedCandlesAreCapturedWithoutPrivateEnvelopeFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "research-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await MT5BridgeHostTestFixture.StartAsync(
                MT5BridgeHostTestFixture.ValidOptions() with { ResearchCaptureDirectory = directory });
            Assert.False(host.Settings.Host.ExternalExecutionEnabled);
            var message = new MT5CompletedBarMessage(host.Envelope(),
                new("GOLD.test", "XAUUSD", TimeSpan.FromMinutes(5), host.Clock.GetUtcNow().AddMinutes(-5),
                    2000, 2001, 1999, 2000, 2000, 2000.1m, 10));
            using (var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.CompletedBar, message))
            using (var rejected = await host.Client.SendAsync(request))
                Assert.False(rejected.IsSuccessStatusCode);
            Assert.False(Directory.Exists(directory));
            await host.PrepareReadyStateAsync();
            await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.CompletedBar, message);
            host.Clock.Advance(TimeSpan.FromSeconds(1));
            await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.CompletedBar,
                message with { Envelope = host.Envelope() });
            var dataset = new ResearchBarArchive(directory).Export(host.Clock.GetUtcNow());
            Assert.Single(dataset.Bars);
            var json = Encoding.UTF8.GetString(ResearchDatasetFile.Serialize(dataset));
            Assert.DoesNotContain(host.Settings.Identity.AccountIdentifier, json);
            Assert.DoesNotContain(host.Settings.Identity.BridgeInstanceId, json);
            Assert.DoesNotContain(host.Settings.Host.SharedSecret!, json);
            using var ready = await host.Client.GetAsync(MT5BridgeHealthRoutes.Readiness);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedCaptureFailurePreservesAcceptanceHealthObservationAndRequestCompletion(bool loggingFails)
    {
        var directory = Path.Combine(Path.GetTempPath(), "research-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var captureLogger = new RecordingLogger<MT5ResearchCapture>(loggingFails);
            await using var host = await MT5BridgeHostTestFixture.StartAsync(
                MT5BridgeHostTestFixture.ValidOptions() with { ResearchCaptureDirectory = directory },
                builder => builder.Services.AddSingleton<ILogger<MT5ResearchCapture>>(captureLogger));
            using var metrics = new MT5BridgeHealthMetrics(host.State);
            var healthLogger = new RecordingLogger<MT5BridgeHealthMonitor>();
            var monitor = new MT5BridgeHealthMonitor(host.State, metrics, healthLogger);
            await host.PrepareReadyStateAsync();
            var message = new MT5CompletedBarMessage(host.Envelope(),
                new("GOLD.test", "XAUUSD", TimeSpan.FromMinutes(5), host.Clock.GetUtcNow().AddMinutes(-5),
                    decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, 1, 2, 10));

            // Telemetry accepts this candle, but the research quote projection overflows during archive validation.
            Assert.Throws<OverflowException>(() => new ResearchBarArchive(directory).Capture(
                new(1, "XAUUSD", "GOLD.test", ResearchSource.Mt5Telemetry, host.Clock.GetUtcNow(),
                    [new(message.Bar.OpenTimeUtc, host.Clock.GetUtcNow(), host.Clock.GetUtcNow(), 5,
                        decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, 10, 1)])));
            var ingestion = new MT5BridgeIngestionService(host.State, host.Transport, monitor,
                host.Application.Services.GetRequiredService<MT5ResearchCapture>());
            Assert.Equal(204, ingestion.Accept(message).StatusCode);
            // This separate monitor has no supervisor; the transition proves ingestion called Observe.
            Assert.Contains(healthLogger.Messages, entry => entry.Message.Contains("readiness changed to ready"));
            Assert.Single(captureLogger.Messages, entry => entry.Level == LogLevel.Error);

            host.Clock.Advance(TimeSpan.FromSeconds(1));
            using var request = host.SignedRequest(HttpMethod.Post, MT5BridgeRoutes.CompletedBar,
                message with { Envelope = host.Envelope() });
            using var accepted = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            Assert.Single(captureLogger.Messages, entry => entry.Level == LogLevel.Error);
            using var ready = await host.Client.GetAsync(MT5BridgeHealthRoutes.Readiness);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.False(host.Settings.Host.ExternalExecutionEnabled);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CaptureDoesNotSwallowOutOfMemoryFailure()
    {
        var logger = new RecordingLogger<MT5ResearchCapture>();
        var capture = new MT5ResearchCapture(MT5BridgeHostTestFixture.ValidOptions() with
            { ResearchCaptureDirectory = Path.GetTempPath() }, new FatalClock(), logger);
        var message = new MT5CompletedBarMessage(
            new(MT5BridgeProtocol.CurrentVersion, "", MT5BridgeHostTestFixture.InitialTime),
            new("GOLD.test", "XAUUSD", TimeSpan.FromMinutes(5),
                MT5BridgeHostTestFixture.InitialTime.AddMinutes(-5), 2000, 2001, 1999, 2000, 2000, 2000.1m, 10));
        Assert.Throws<OutOfMemoryException>(() => capture.CaptureAccepted(message));
        Assert.Empty(logger.Messages);
    }

    private sealed class FatalClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new OutOfMemoryException("Simulated fatal failure.");
    }

    private sealed class RecordingLogger<T>(bool throwOnLog = false) : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue((logLevel, formatter(state, exception)));
            if (throwOnLog) throw new NotSupportedException("Simulated logging failure.");
        }
    }

    [Fact]
    public async Task StorageFailureDoesNotChangeTelemetryAcceptanceOrReadiness()
    {
        var file = Path.GetTempFileName();
        try
        {
            await using var host = await MT5BridgeHostTestFixture.StartAsync(
                MT5BridgeHostTestFixture.ValidOptions() with { ResearchCaptureDirectory = file });
            await host.PrepareReadyStateAsync();
            await host.SendAcceptedAsync(HttpMethod.Post, MT5BridgeRoutes.CompletedBar,
                new MT5CompletedBarMessage(host.Envelope(),
                    new("GOLD.test", "XAUUSD", TimeSpan.FromMinutes(5), host.Clock.GetUtcNow().AddMinutes(-5),
                        2000, 2001, 1999, 2000, 2000, 2000.1m, 10)));
            using var ready = await host.Client.GetAsync(MT5BridgeHealthRoutes.Readiness);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.False(host.Settings.Host.ExternalExecutionEnabled);
        }
        finally { File.Delete(file); }
    }
}
