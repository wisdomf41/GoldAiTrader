using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.MT5.BridgeHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoldAiTrader.Core.Tests;

[Collection("MT5 health supervision")]
public sealed class MT5BridgeHealthSupervisionTests
{
    [Fact]
    public async Task SupervisorDetectsStalenessAndRecoveryWithoutRequestsAndCountsOnlyTransitions()
    {
        var clock = new SupervisionTimeProvider();
        var state = new MT5BridgeState(MT5BridgeHostTestFixture.Identity(),
            MT5BridgeHostTestFixture.Mappings(), timeProvider: clock);
        var measurements = new ConcurrentQueue<(string Instrument, long Value, string Tag)>();
        var constructionThread = Environment.CurrentManagedThreadId;
        var captureInstruments = true;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            // Only subscribe to this test's meter, including during parallel test runs.
            if (captureInstruments && Environment.CurrentManagedThreadId == constructionThread &&
                instrument.Meter.Name == MT5BridgeHealthMetrics.MeterName &&
                instrument is Counter<long>)
                activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, Assert.Single(tags.ToArray()).Value!.ToString()!)));
        listener.Start();
        using var metrics = new MT5BridgeHealthMetrics(state);
        captureInstruments = false;
        var logger = new CapturingLogger();
        var monitor = new MT5BridgeHealthMonitor(state, metrics, logger);
        using var supervisor = new MT5BridgeHealthSupervisor(monitor,
            MT5BridgeHostTestFixture.ValidOptions(), clock);

        await supervisor.StartAsync(CancellationToken.None);
        try
        {
            await clock.WaitForTimerAsync();
            await WaitUntilAsync(() => clock.Reads >= 2);
            Assert.Empty(measurements);
            Assert.Empty(logger.Messages);

            Refresh(state, clock);
            await TickAsync(clock);
            await WaitUntilAsync(() => logger.Messages.Count == 2);
            Assert.Equal(1, Count("connected"));
            Assert.Equal(1, Count("ready"));

            clock.Advance(TimeSpan.FromSeconds(16));
            await TickAsync(clock);
            await WaitUntilAsync(() => logger.Messages.Count == 3);
            Assert.Equal(1, Count("not_ready"));
            Assert.Equal(1, Count("heartbeat_stale"));

            for (var i = 0; i < 5; i++)
                await TickAsync(clock);
            Assert.Equal(3, logger.Messages.Count);
            Assert.Equal(4, measurements.Count);

            Refresh(state, clock);
            await TickAsync(clock);
            await WaitUntilAsync(() => logger.Messages.Count == 4);
            Assert.Equal(2, Count("ready"));
            for (var i = 0; i < 5; i++)
                await TickAsync(clock);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(supervisor.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(clock.TimerDisposed);
        Assert.Equal(4, logger.Messages.Count);
        Assert.Equal(5, measurements.Count);
        Assert.All(measurements, measurement => Assert.Equal(1, measurement.Value));
        Assert.Single(logger.Messages, message => message.Contains("not_ready (heartbeat_stale)"));
        var readsAfterStop = clock.Reads;
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Tick();
        Assert.Equal(readsAfterStop, clock.Reads);

        long Count(string tag) => measurements.Where(measurement => measurement.Tag == tag)
            .Sum(measurement => measurement.Value);
    }

    [Fact]
    public async Task RegisteredSupervisorStopsWithApplicationWhileWaitingForNextTick()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync(
            MT5BridgeHostTestFixture.ValidOptions() with
            {
                HealthSupervisionInterval = TimeSpan.FromSeconds(30)
            });
        var supervisor = Assert.Single(host.Application.Services.GetServices<IHostedService>()
            .OfType<MT5BridgeHealthSupervisor>());
        await host.Application.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(supervisor.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("http://collector.test:4318", "http://collector.test:4318/v1/metrics")]
    [InlineData("http://collector.test:4318/otlp/", "http://collector.test:4318/otlp/v1/metrics")]
    public async Task OtlpExporterPublishesBridgeMeterUsingStandardConfiguration(
        string endpoint, string expectedRequestEndpoint)
    {
        using var handler = new CapturingOtlpHandler();
        await using var host = await MT5BridgeHostTestFixture.StartAsync(configure: builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf"
            });
            builder.Services.Configure<OtlpExporterOptions>(exporter =>
                exporter.HttpClientFactory = () => new HttpClient(handler, disposeHandler: false));
        });
        await host.PrepareReadyStateAsync();
        Assert.False(host.Settings.Host.ExternalExecutionEnabled);

        using var unrelatedMeter = new Meter("Unrelated.Test.Meter");
        unrelatedMeter.CreateCounter<long>("unrelated.test.counter").Add(1);
        var provider = host.Application.Services.GetRequiredService<MeterProvider>();
        Assert.True(provider.ForceFlush(5000));

        var exported = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, exported.Method);
        Assert.Equal(new Uri(expectedRequestEndpoint), exported.Endpoint);
        Assert.Equal("application/x-protobuf", exported.ContentType);
        Assert.NotEmpty(exported.Body);
        // OTLP protobuf string fields retain their UTF-8 bytes on the wire.
        var payload = Encoding.UTF8.GetString(exported.Body);
        Assert.Contains(MT5BridgeHealthMetrics.MeterName, payload);
        foreach (var instrument in new[]
        {
            "goldaitrader.mt5.terminal.connected", "goldaitrader.mt5.bridge.ready",
            "goldaitrader.mt5.heartbeat.age", "goldaitrader.mt5.heartbeat.fresh",
            "goldaitrader.mt5.health.transitions", "goldaitrader.mt5.telemetry.freshness.failures"
        })
            Assert.Contains(instrument, payload);
        Assert.Contains("transition", payload);
        Assert.Contains("reason", payload);
        Assert.DoesNotContain("unrelated.test.counter", payload);
        Assert.DoesNotContain("service.instance.id", payload);
        foreach (var sensitive in new[]
        {
            MT5BridgeHostTestFixture.Secret, host.Settings.Identity.AccountIdentifier,
            host.Settings.Identity.BridgeInstanceId, host.Settings.Identity.TerminalInstanceId,
            host.Settings.Identity.BrokerName, "XAUUSD", "GOLD.test"
        })
            Assert.DoesNotContain(sensitive, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeHostDoesNotExposeDirectPrometheusEndpoint()
    {
        await using var host = await MT5BridgeHostTestFixture.StartAsync();
        using var response = await host.Client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(host.Settings.Host.ExternalExecutionEnabled);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(30001)]
    public void SupervisionIntervalRejectsUnboundedValues(int milliseconds)
    {
        var options = MT5BridgeHostTestFixture.ValidOptions() with
        {
            HealthSupervisionInterval = TimeSpan.FromMilliseconds(milliseconds)
        };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(30000)]
    public void SupervisionIntervalAcceptsBoundedValues(int milliseconds)
    {
        (MT5BridgeHostTestFixture.ValidOptions() with
        {
            HealthSupervisionInterval = TimeSpan.FromMilliseconds(milliseconds)
        }).Validate();
    }

    [Fact]
    public void SupervisionIntervalLoadsFromConfigurationAndDefaultsToOneSecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(1),
            MT5BridgeHostSettings.FromConfiguration(new ConfigurationBuilder().Build())
                .Host.HealthSupervisionInterval);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MT5Bridge:HealthSupervisionIntervalSeconds"] = "0.25"
            }).Build();
        Assert.Equal(TimeSpan.FromMilliseconds(250),
            MT5BridgeHostSettings.FromConfiguration(configuration).Host.HealthSupervisionInterval);
    }

    private static void Refresh(MT5BridgeState state, TimeProvider clock)
    {
        var envelope = new MT5MessageEnvelope(state.Identity.ProtocolVersion,
            state.Identity.BridgeInstanceId, clock.GetUtcNow());
        state.AcceptHeartbeat(new(envelope, true));
        state.AcceptAccount(new(envelope, MT5BridgeHostTestFixture.Account()));
        state.AcceptSymbol(new(envelope, MT5BridgeHostTestFixture.Symbol()));
        state.AcceptPositions(new(envelope, state.Identity.AccountIdentifier, []));
    }

    private static async Task TickAsync(SupervisionTimeProvider clock)
    {
        var before = clock.Reads;
        clock.Tick();
        await WaitUntilAsync(() => clock.Reads > before);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class CapturingLogger : ILogger<MT5BridgeHealthMonitor>
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class SupervisionTimeProvider : TimeProvider
    {
        private long utcTicks = MT5BridgeHostTestFixture.InitialTime.UtcTicks;
        private int reads;
        private readonly TaskCompletionSource<ManualTimer> timer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads => Volatile.Read(ref reads);
        internal bool TimerDisposed => timer.Task.Result.Disposed;
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref reads);
            return new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
        }
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref utcTicks, duration.Ticks);
        internal Task WaitForTimerAsync() => timer.Task.WaitAsync(TimeSpan.FromSeconds(5));
        internal void Tick() => timer.Task.Result.Tick();
        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), dueTime);
            Assert.Equal(dueTime, period);
            var result = new ManualTimer(callback, state);
            timer.SetResult(result);
            return result;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed { get; private set; }
            internal void Tick()
            {
                if (!Disposed)
                    callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

// Export assertions require a single host meter in the process.
[CollectionDefinition("MT5 health supervision", DisableParallelization = true)]
public sealed class MT5HealthSupervisionCollection;
