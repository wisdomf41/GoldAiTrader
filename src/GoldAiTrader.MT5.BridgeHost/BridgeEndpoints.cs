using System.Text.Json;
using GoldAiTrader.Adapters.MT5;

namespace GoldAiTrader.MT5.BridgeHost;

public static class MT5BridgeRoutes
{
    public const string Prefix = "/bridge/mt5/v1";
    public const string Heartbeat = Prefix + "/heartbeat";
    public const string Connection = Prefix + "/connection";
    public const string Account = Prefix + "/account";
    public const string Symbol = Prefix + "/symbol";
    public const string Positions = Prefix + "/positions";
    public const string CompletedBar = Prefix + "/bars/completed";
    public const string NextCommand = Prefix + "/commands/next";
    public const string ExecutionAcknowledgement = Prefix + "/execution/ack";
}

public sealed record MT5CommandPollResponse(string CommandType, object Command);

public static class MT5BridgeEndpointExtensions
{
    public static IEndpointRouteBuilder MapMT5BridgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(MT5BridgeRoutes.Heartbeat, HandleHeartbeatAsync);
        endpoints.MapPost(MT5BridgeRoutes.Connection, HandleConnectionAsync);
        endpoints.MapPost(MT5BridgeRoutes.Account, HandleAccountAsync);
        endpoints.MapPost(MT5BridgeRoutes.Symbol, HandleSymbolAsync);
        endpoints.MapPost(MT5BridgeRoutes.Positions, HandlePositionsAsync);
        endpoints.MapPost(MT5BridgeRoutes.CompletedBar, HandleCompletedBarAsync);
        endpoints.MapGet(MT5BridgeRoutes.NextCommand, HandleNextCommand);
        endpoints.MapPost(MT5BridgeRoutes.ExecutionAcknowledgement,
            HandleExecutionAcknowledgementAsync);
        return endpoints;
    }

    private static Task<IResult> HandleHeartbeatAsync(HttpContext context,
        MT5BridgeRequestProcessor processor, MT5BridgeIngestionService ingestion) =>
        processor.ProcessAsync<MT5HeartbeatMessage>(context, ingestion.Accept);

    private static Task<IResult> HandleConnectionAsync(HttpContext context,
        MT5BridgeRequestProcessor processor, MT5BridgeIngestionService ingestion) =>
        processor.ProcessAsync<MT5ConnectionStateMessage>(context, ingestion.Accept);

    private static Task<IResult> HandleAccountAsync(HttpContext context,
        MT5BridgeRequestProcessor processor, MT5BridgeIngestionService ingestion) =>
        processor.ProcessAsync<MT5AccountMessage>(context, ingestion.Accept);

    private static Task<IResult> HandleSymbolAsync(HttpContext context,
        MT5BridgeRequestProcessor processor, MT5BridgeIngestionService ingestion) =>
        processor.ProcessAsync<MT5SymbolMessage>(context, ingestion.Accept);

    private static Task<IResult> HandlePositionsAsync(HttpContext context,
        MT5BridgeRequestProcessor processor, MT5BridgeIngestionService ingestion) =>
        processor.ProcessAsync<MT5PositionInventoryMessage>(context, ingestion.Accept);

    private static Task<IResult> HandleCompletedBarAsync(HttpContext context,
        MT5BridgeRequestProcessor processor, MT5BridgeIngestionService ingestion) =>
        processor.ProcessAsync<MT5CompletedBarMessage>(context, ingestion.Accept);

    private static Task<IResult> HandleExecutionAcknowledgementAsync(HttpContext context,
        MT5BridgeRequestProcessor processor, MT5BridgeIngestionService ingestion) =>
        processor.ProcessAsync<MT5ExecutionAcknowledgement>(context, ingestion.Accept);

    private static IResult HandleNextCommand(HttpContext context,
        MT5BridgeRequestProcessor processor, MT5PollingExecutionTransport transport,
        JsonSerializerOptions jsonOptions)
    {
        var authentication = processor.Authenticate(context, []);
        if (!authentication.Authenticated)
            return processor.Error(authentication.StatusCode, authentication.Error!.Code,
                authentication.Error.Message);

        var bridgeInstanceId = context.Request.Headers[
            MT5BridgeAuthenticationHeaders.BridgeInstanceId].ToString();
        var protocolVersion = context.Request.Headers[
            MT5BridgeAuthenticationHeaders.ProtocolVersion].ToString();
        if (!transport.TryPollNext(bridgeInstanceId, protocolVersion, out var command))
            return Results.NoContent();

        return Results.Json(new MT5CommandPollResponse(GetCommandType(command!), command!),
            jsonOptions);
    }

    private static string GetCommandType(MT5ExecutionCommand command) =>
        command switch
        {
            MT5SubmitMarketOrderCommand => "submitMarketOrder",
            MT5ClosePositionCommand => "closePosition",
            MT5ModifyPositionCommand => "modifyPosition",
            _ => throw new InvalidOperationException("Unsupported MT5 command type.")
        };
}
