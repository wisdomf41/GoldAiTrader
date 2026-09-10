using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GoldAiTrader.Adapters.MT5;
using Microsoft.Extensions.Primitives;

namespace GoldAiTrader.MT5.BridgeHost;

public static class MT5BridgeAuthenticationHeaders
{
    public const string BridgeInstanceId = "X-GAT-Bridge-Id";
    public const string ProtocolVersion = "X-GAT-Protocol-Version";
    public const string Timestamp = "X-GAT-Timestamp";
    public const string Nonce = "X-GAT-Nonce";
    public const string Signature = "X-GAT-Signature";
}

public sealed record MT5BridgeError(string Code, string Message);

public sealed record MT5AuthenticationResult(
    bool Authenticated,
    int StatusCode,
    MT5BridgeError? Error = null)
{
    public static MT5AuthenticationResult Success { get; } = new(true, StatusCodes.Status200OK);

    public static MT5AuthenticationResult Reject(int statusCode, string code, string message) =>
        new(false, statusCode, new(code, message));
}

public static class MT5RequestSigner
{
    public static string Sign(string secret, string bridgeInstanceId, string protocolVersion,
        string method, string path, string timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var canonical = CreateCanonicalPayload(bridgeInstanceId, protocolVersion, method,
            path, timestamp, nonce, body);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), canonical);
        return Convert.ToHexStringLower(signature);
    }

    public static byte[] CreateCanonicalPayload(string bridgeInstanceId, string protocolVersion,
        string method, string path, string timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));
        var value = string.Join('\n', bridgeInstanceId, protocolVersion,
            method.ToUpperInvariant(), path, timestamp, nonce, bodyHash);
        return Encoding.UTF8.GetBytes(value);
    }
}

public enum MT5ReplayDecision
{
    Accepted,
    Duplicate,
    CapacityExceeded
}

public sealed class MT5RequestReplayCache(MT5BridgeHostOptions options, TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Dictionary<string, DateTimeOffset> entries = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (sync)
            {
                PurgeExpiredLocked(timeProvider.GetUtcNow().ToUniversalTime());
                return entries.Count;
            }
        }
    }

    public MT5ReplayDecision TryAccept(string bridgeInstanceId, string nonce,
        DateTimeOffset requestTimestampUtc)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var key = $"{bridgeInstanceId}\n{nonce}";
        lock (sync)
        {
            PurgeExpiredLocked(now);
            if (entries.ContainsKey(key))
                return MT5ReplayDecision.Duplicate;
            if (entries.Count >= options.ReplayCacheCapacity)
                return MT5ReplayDecision.CapacityExceeded;

            entries.Add(key, requestTimestampUtc + options.AuthenticationTolerance);
            return MT5ReplayDecision.Accepted;
        }
    }

    private void PurgeExpiredLocked(DateTimeOffset now)
    {
        foreach (var key in entries.Where(entry => entry.Value < now)
                     .Select(entry => entry.Key).ToArray())
            entries.Remove(key);
    }
}

public sealed class MT5AuthenticatedSession(
    MT5BridgeIdentity identity,
    MT5BridgeHostOptions options,
    TimeProvider timeProvider)
{
    private readonly object sync = new();
    private DateTimeOffset? lastAuthenticatedAtUtc;

    public void Observe(string bridgeInstanceId, string protocolVersion)
    {
        if (!string.Equals(bridgeInstanceId, identity.BridgeInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(protocolVersion, identity.ProtocolVersion,
                StringComparison.Ordinal))
            throw new InvalidOperationException("Authenticated MT5 session identity is invalid.");

        lock (sync)
            lastAuthenticatedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
    }

    public bool IsCurrent
    {
        get
        {
            lock (sync)
            {
                if (lastAuthenticatedAtUtc is null)
                    return false;
                var age = timeProvider.GetUtcNow().ToUniversalTime() -
                    lastAuthenticatedAtUtc.Value;
                return age >= TimeSpan.Zero && age <= options.AuthenticationTolerance;
            }
        }
    }
}

public sealed class MT5RequestAuthenticator(
    MT5BridgeHostOptions options,
    MT5BridgeIdentity identity,
    MT5RequestReplayCache replayCache,
    TimeProvider timeProvider,
    ILogger<MT5RequestAuthenticator> logger)
{
    public MT5AuthenticationResult Authenticate(HttpRequest request, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.QueryString.HasValue)
            return Reject(StatusCodes.Status400BadRequest, "query_not_allowed",
                "Query strings are not accepted by the MT5 bridge.", null);

        if (!TryGetSingleHeader(request, MT5BridgeAuthenticationHeaders.BridgeInstanceId,
                out var bridgeInstanceId) ||
            !TryGetSingleHeader(request, MT5BridgeAuthenticationHeaders.ProtocolVersion,
                out var protocolVersion) ||
            !TryGetSingleHeader(request, MT5BridgeAuthenticationHeaders.Timestamp,
                out var timestampText) ||
            !TryGetSingleHeader(request, MT5BridgeAuthenticationHeaders.Nonce, out var nonce) ||
            !TryGetSingleHeader(request, MT5BridgeAuthenticationHeaders.Signature,
                out var signatureText))
            return Reject(StatusCodes.Status401Unauthorized, "authentication_headers_missing",
                "Required MT5 authentication headers are missing or ambiguous.", null);

        if (!string.Equals(bridgeInstanceId, identity.BridgeInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(protocolVersion, identity.ProtocolVersion,
                StringComparison.Ordinal))
            return Reject(StatusCodes.Status401Unauthorized, "bridge_identity_invalid",
                "MT5 bridge identity or protocol version is invalid.", bridgeInstanceId);

        if (!IsValidNonce(nonce))
            return Reject(StatusCodes.Status401Unauthorized, "nonce_invalid",
                "MT5 request nonce is malformed.", bridgeInstanceId);

        if (!DateTimeOffset.TryParseExact(timestampText, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp) || timestamp.Offset != TimeSpan.Zero)
            return Reject(StatusCodes.Status401Unauthorized, "timestamp_invalid",
                "MT5 request timestamp must be an exact UTC round-trip value.", bridgeInstanceId);

        var age = timeProvider.GetUtcNow().ToUniversalTime() - timestamp;
        if (age < -options.AuthenticationTolerance || age > options.AuthenticationTolerance)
            return Reject(StatusCodes.Status401Unauthorized, "timestamp_outside_window",
                "MT5 request timestamp is outside the trusted window.", bridgeInstanceId);

        if (!TryDecodeSignature(signatureText, out var suppliedSignature))
            return Reject(StatusCodes.Status401Unauthorized, "signature_malformed",
                "MT5 request signature is malformed.", bridgeInstanceId);

        var expectedHex = MT5RequestSigner.Sign(options.SharedSecret!, bridgeInstanceId,
            protocolVersion, request.Method, request.Path.Value!, timestampText, nonce, body);
        var expectedSignature = Convert.FromHexString(expectedHex);
        if (!CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
            return Reject(StatusCodes.Status401Unauthorized, "signature_invalid",
                "MT5 request signature is invalid.", bridgeInstanceId);

        var replay = replayCache.TryAccept(bridgeInstanceId, nonce, timestamp);
        if (replay != MT5ReplayDecision.Accepted)
            return Reject(StatusCodes.Status409Conflict,
                replay == MT5ReplayDecision.Duplicate ? "request_replayed" : "replay_cache_full",
                replay == MT5ReplayDecision.Duplicate
                    ? "MT5 request nonce has already been accepted."
                    : "MT5 replay cache is at capacity; request rejected safely.",
                bridgeInstanceId);

        logger.LogDebug("Authenticated MT5 bridge request {Method} {Path} for {BridgeId}.",
            request.Method, request.Path.Value, bridgeInstanceId);
        return MT5AuthenticationResult.Success;
    }

    private MT5AuthenticationResult Reject(int statusCode, string code,
        string message, string? bridgeInstanceId)
    {
        logger.LogWarning("Rejected MT5 bridge request: {Code}; bridge {BridgeId}.",
            code, bridgeInstanceId ?? "unknown");
        return MT5AuthenticationResult.Reject(statusCode, code, message);
    }

    private static bool TryGetSingleHeader(HttpRequest request, string name, out string value)
    {
        value = string.Empty;
        if (!request.Headers.TryGetValue(name, out StringValues values) || values.Count != 1)
            return false;
        var candidate = values[0];
        if (string.IsNullOrWhiteSpace(candidate) ||
            !string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
            return false;
        value = candidate;
        return true;
    }

    private static bool IsValidNonce(string nonce) =>
        nonce.Length is >= 16 and <= 128 &&
        nonce.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool TryDecodeSignature(string value, out byte[] signature)
    {
        signature = [];
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            return false;
        try
        {
            signature = Convert.FromHexString(value);
            return signature.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
