using System.Globalization;
using System.Text;
using GoldAiTrader.Adapters.MT5;

namespace GoldAiTrader.MT5.BridgeHost;

public sealed record MT5BridgeHostOptions
{
    public const string ConfigurationSection = "MT5Bridge";
    public const string SecretEnvironmentVariable = "GOLDAITRADER_MT5_BRIDGE_SECRET";

    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5088;
    public string? SharedSecret { get; init; }
    public TimeSpan AuthenticationTolerance { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumRequestBodyBytes { get; init; } = 256 * 1024;
    public int MaximumJsonDepth { get; init; } = 32;
    public int ReplayCacheCapacity { get; init; } = 4096;
    public int PendingCommandCapacity { get; init; } = 128;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool ExternalExecutionEnabled { get; init; }

    public MT5LoopbackEndpoint Endpoint =>
        new($"http://{BindAddress}:{Port}");

    public void Validate()
    {
        if (!string.Equals(BindAddress, "127.0.0.1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The MT5 bridge host must bind to the explicit IPv4 address 127.0.0.1.");
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException("The MT5 bridge host port is invalid.");
        if (string.IsNullOrWhiteSpace(SharedSecret) ||
            Encoding.UTF8.GetByteCount(SharedSecret) < 32)
            throw new InvalidOperationException(
                "A configured MT5 bridge secret of at least 32 UTF-8 bytes is required.");
        if (AuthenticationTolerance < TimeSpan.FromSeconds(1) ||
            AuthenticationTolerance > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException(
                "MT5 authentication tolerance must be between one second and five minutes.");
        if (MaximumRequestBodyBytes is < 1024 or > 1024 * 1024)
            throw new InvalidOperationException(
                "MT5 request-body limit must be between 1 KiB and 1 MiB.");
        if (MaximumJsonDepth is < 4 or > 64)
            throw new InvalidOperationException("MT5 JSON depth limit must be between 4 and 64.");
        if (ReplayCacheCapacity is < 1 or > 100_000)
            throw new InvalidOperationException("MT5 replay-cache capacity is invalid.");
        if (PendingCommandCapacity is < 1 or > 4096)
            throw new InvalidOperationException("MT5 pending-command capacity is invalid.");
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException(
                "MT5 command timeout must be positive and no greater than five minutes.");
    }
}

public sealed record MT5BridgeHostSettings(
    MT5BridgeHostOptions Host,
    MT5BridgeIdentity Identity,
    IReadOnlyDictionary<string, string> SymbolMappings)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Host);
        ArgumentNullException.ThrowIfNull(Identity);
        ArgumentNullException.ThrowIfNull(SymbolMappings);
        Host.Validate();
        MT5BridgeProtocol.ValidateIdentity(Identity);
        _ = new MT5SymbolMapper(SymbolMappings);
        if (SymbolMappings.Count == 0)
            throw new InvalidOperationException(
                "At least one canonical-to-broker MT5 symbol mapping is required.");
    }

    public static MT5BridgeHostSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(MT5BridgeHostOptions.ConfigurationSection);
        var identity = section.GetSection("Identity");
        var mappings = section.GetSection("SymbolMappings").GetChildren()
            .ToDictionary(child => child.Key, child => child.Value ?? string.Empty,
                StringComparer.Ordinal);

        var host = new MT5BridgeHostOptions
        {
            BindAddress = section["BindAddress"] ?? "127.0.0.1",
            Port = ParseInt(section["Port"], 5088),
            SharedSecret = configuration[MT5BridgeHostOptions.SecretEnvironmentVariable] ??
                section.GetSection("Host")["SharedSecret"] ??
                section["SharedSecret"],
            AuthenticationTolerance = TimeSpan.FromSeconds(
                ParseDouble(section["AuthenticationToleranceSeconds"], 30)),
            MaximumRequestBodyBytes = ParseInt(section["MaximumRequestBodyBytes"], 256 * 1024),
            MaximumJsonDepth = ParseInt(section["MaximumJsonDepth"], 32),
            ReplayCacheCapacity = ParseInt(section["ReplayCacheCapacity"], 4096),
            PendingCommandCapacity = ParseInt(section["PendingCommandCapacity"], 128),
            CommandTimeout = TimeSpan.FromSeconds(
                ParseDouble(section["CommandTimeoutSeconds"], 30)),
            ExternalExecutionEnabled = ParseBool(section["ExternalExecutionEnabled"], false)
        };

        return new(host, new(
            identity["BridgeInstanceId"] ?? string.Empty,
            identity["TerminalInstanceId"] ?? string.Empty,
            identity["BrokerName"] ?? string.Empty,
            identity["AccountIdentifier"] ?? string.Empty,
            identity["AdapterVersion"] ?? "1.0.0",
            identity["ProtocolVersion"] ?? MT5BridgeProtocol.CurrentVersion), mappings);
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool ParseBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;
}
