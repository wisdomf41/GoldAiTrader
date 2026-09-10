using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldAiTrader.Adapters.MT5;

namespace GoldAiTrader.MT5.BridgeHost;

public sealed record MT5IngestionResult(int StatusCode, MT5BridgeError? Error = null)
{
    public static MT5IngestionResult Accepted { get; } =
        new(StatusCodes.Status204NoContent);

    public static MT5IngestionResult Reject(int statusCode, string code, string message) =>
        new(statusCode, new(code, message));
}

public static class MT5BridgeJson
{
    public static JsonSerializerOptions CreateOptions(int maximumDepth)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = maximumDepth
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

public interface IMT5BridgeIngestionService
{
    MT5IngestionResult Accept(MT5HeartbeatMessage message);
    MT5IngestionResult Accept(MT5ConnectionStateMessage message);
    MT5IngestionResult Accept(MT5AccountMessage message);
    MT5IngestionResult Accept(MT5SymbolMessage message);
    MT5IngestionResult Accept(MT5PositionInventoryMessage message);
    MT5IngestionResult Accept(MT5CompletedBarMessage message);
    MT5IngestionResult Accept(MT5ExecutionAcknowledgement acknowledgement);
}

public sealed class MT5BridgeIngestionService(
    MT5BridgeState state,
    MT5PollingExecutionTransport executionTransport) : IMT5BridgeIngestionService
{
    public MT5IngestionResult Accept(MT5HeartbeatMessage message)
    {
        state.AcceptHeartbeat(message);
        return MT5IngestionResult.Accepted;
    }

    public MT5IngestionResult Accept(MT5ConnectionStateMessage message)
    {
        state.AcceptConnectionState(message);
        return MT5IngestionResult.Accepted;
    }

    public MT5IngestionResult Accept(MT5AccountMessage message)
    {
        state.AcceptAccount(message);
        return MT5IngestionResult.Accepted;
    }

    public MT5IngestionResult Accept(MT5SymbolMessage message)
    {
        state.AcceptSymbol(message);
        return MT5IngestionResult.Accepted;
    }

    public MT5IngestionResult Accept(MT5PositionInventoryMessage message)
    {
        state.AcceptPositions(message);
        return MT5IngestionResult.Accepted;
    }

    public MT5IngestionResult Accept(MT5CompletedBarMessage message)
    {
        state.AcceptCompletedBar(message);
        return MT5IngestionResult.Accepted;
    }

    public MT5IngestionResult Accept(MT5ExecutionAcknowledgement acknowledgement) =>
        executionTransport.AcceptAcknowledgement(acknowledgement) switch
        {
            MT5AcknowledgementDecision.Accepted => MT5IngestionResult.Accepted,
            MT5AcknowledgementDecision.Duplicate => MT5IngestionResult.Reject(
                StatusCodes.Status409Conflict, "acknowledgement_duplicate",
                "The execution acknowledgement was already consumed."),
            MT5AcknowledgementDecision.UnknownCommand => MT5IngestionResult.Reject(
                StatusCodes.Status409Conflict, "acknowledgement_unknown",
                "The execution acknowledgement does not match a pending command."),
            MT5AcknowledgementDecision.CommandNotDelivered => MT5IngestionResult.Reject(
                StatusCodes.Status409Conflict, "command_not_delivered",
                "The command has not been delivered to the bridge."),
            MT5AcknowledgementDecision.IndeterminateCommand => MT5IngestionResult.Reject(
                StatusCodes.Status409Conflict, "acknowledgement_indeterminate",
                "The command already ended with an indeterminate broker outcome; reconciliation remains authoritative."),
            _ => MT5IngestionResult.Reject(StatusCodes.Status422UnprocessableEntity,
                "acknowledgement_invalid", "The execution acknowledgement is invalid.")
        };
}

public sealed class MT5BridgeRequestProcessor(
    MT5BridgeHostOptions options,
    MT5RequestAuthenticator authenticator,
    MT5AuthenticatedSession session,
    JsonSerializerOptions jsonOptions,
    ILogger<MT5BridgeRequestProcessor> logger)
{
    public async Task<IResult> ProcessAsync<T>(HttpContext context,
        Func<T, MT5IngestionResult> apply) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(apply);

        if (!context.Request.HasJsonContentType())
            return Error(StatusCodes.Status415UnsupportedMediaType, "content_type_invalid",
                "MT5 bridge POST requests require application/json.");

        var bodyResult = await ReadBodyAsync(context.Request, context.RequestAborted);
        if (bodyResult.Error is not null)
            return Error(bodyResult.StatusCode, bodyResult.Error.Code, bodyResult.Error.Message);

        var authentication = authenticator.Authenticate(context.Request, bodyResult.Body!);
        if (!authentication.Authenticated)
            return Error(authentication.StatusCode, authentication.Error!.Code,
                authentication.Error.Message);

        T? message;
        try
        {
            message = JsonSerializer.Deserialize<T>(bodyResult.Body!, jsonOptions);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "json_invalid",
                "The MT5 bridge JSON payload is malformed or unsupported.");
        }

        if (message is null)
            return Error(StatusCodes.Status400BadRequest, "json_invalid",
                "The MT5 bridge JSON payload is required.");

        try
        {
            var result = apply(message);
            if (result.Error is null)
                session.Observe(
                    context.Request.Headers[MT5BridgeAuthenticationHeaders.BridgeInstanceId]
                        .ToString(),
                    context.Request.Headers[MT5BridgeAuthenticationHeaders.ProtocolVersion]
                        .ToString());
            return result.Error is null
                ? Results.StatusCode(result.StatusCode)
                : Error(result.StatusCode, result.Error.Code, result.Error.Message);
        }
        catch (NotSupportedException)
        {
            return Error(StatusCodes.Status400BadRequest, "protocol_version_invalid",
                "The MT5 bridge protocol version is not supported.");
        }
        catch (ArgumentException)
        {
            return Error(StatusCodes.Status400BadRequest, "protocol_message_invalid",
                "The MT5 bridge message contains invalid required values.");
        }
        catch (InvalidOperationException exception)
        {
            var conflict = exception.Message.Contains("duplicat", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("older", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("replay", StringComparison.OrdinalIgnoreCase);
            logger.LogWarning("MT5 bridge state rejected {MessageType} as {Category}.",
                typeof(T).Name, conflict ? "conflict" : "unsafe");
            return conflict
                ? Error(StatusCodes.Status409Conflict, "bridge_state_conflict",
                    "The message conflicts with newer trusted bridge state.")
                : Error(StatusCodes.Status422UnprocessableEntity, "bridge_state_unsafe",
                    "The message cannot be safely applied to bridge state.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogError("Unexpected MT5 bridge ingestion failure for {MessageType}.",
                typeof(T).Name);
            return Error(StatusCodes.Status500InternalServerError, "bridge_internal_error",
                "The MT5 bridge request could not be processed safely.");
        }

    }

    public IResult AuthenticateEmpty(HttpContext context)
    {
        var authentication = authenticator.Authenticate(context.Request, []);
        return authentication.Authenticated
            ? Results.Ok()
            : Error(authentication.StatusCode, authentication.Error!.Code,
                authentication.Error.Message);
    }

    public MT5AuthenticationResult Authenticate(HttpContext context, ReadOnlySpan<byte> body) =>
        authenticator.Authenticate(context.Request, body);

    public IResult Error(int statusCode, string code, string message) =>
        Results.Json(new MT5BridgeError(code, message), jsonOptions,
            statusCode: statusCode);

    private async Task<BodyReadResult> ReadBodyAsync(HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > options.MaximumRequestBodyBytes)
            return BodyReadResult.Reject(StatusCodes.Status413PayloadTooLarge,
                "payload_too_large", "The MT5 bridge request body exceeds the configured limit.");

        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                if (destination.Length + read > options.MaximumRequestBodyBytes)
                    return BodyReadResult.Reject(StatusCodes.Status413PayloadTooLarge,
                        "payload_too_large",
                        "The MT5 bridge request body exceeds the configured limit.");
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        return BodyReadResult.Success(destination.ToArray());
    }

    private sealed record BodyReadResult(byte[]? Body, int StatusCode, MT5BridgeError? Error)
    {
        public static BodyReadResult Success(byte[] body) =>
            new(body, StatusCodes.Status200OK, null);

        public static BodyReadResult Reject(int statusCode, string code, string message) =>
            new(null, statusCode, new(code, message));
    }
}
