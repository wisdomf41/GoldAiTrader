using System.Net;
using System.Text.Json;
using GoldAiTrader.Adapters.MT5;
using GoldAiTrader.Platform;

namespace GoldAiTrader.MT5.BridgeHost;

public static class MT5BridgeHostApplication
{
    public static WebApplication Build(WebApplicationBuilder builder,
        MT5BridgeHostSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var clock = timeProvider ?? TimeProvider.System;

        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = settings.Host.MaximumRequestBodyBytes;
            server.Listen(IPAddress.Loopback, settings.Host.Port);
        });

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(settings.Host);
        builder.Services.AddSingleton(settings.Identity);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton(new MT5BridgeState(settings.Identity,
            settings.SymbolMappings, timeProvider: clock));
        builder.Services.AddSingleton<JsonSerializerOptions>(
            MT5BridgeJson.CreateOptions(settings.Host.MaximumJsonDepth));
        builder.Services.AddSingleton<MT5RequestReplayCache>();
        builder.Services.AddSingleton<MT5AuthenticatedSession>();
        builder.Services.AddSingleton<MT5RequestAuthenticator>();
        builder.Services.AddSingleton<MT5BridgeHostRuntime>();
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<MT5BridgeHostRuntime>());
        builder.Services.AddSingleton<MT5PollingExecutionTransport>();
        builder.Services.AddSingleton<IMT5LoopbackExecutionTransport>(services =>
            services.GetRequiredService<MT5PollingExecutionTransport>());
        builder.Services.AddSingleton<ITradingPlatformGateway>(services =>
            new MT5PlatformGateway(
                services.GetRequiredService<MT5BridgeState>(),
                services.GetRequiredService<MT5PollingExecutionTransport>(), clock));
        builder.Services.AddSingleton<MT5BridgeIngestionService>();
        builder.Services.AddSingleton<MT5BridgeRequestProcessor>();

        var app = builder.Build();
        app.MapMT5BridgeEndpoints();
        return app;
    }
}
