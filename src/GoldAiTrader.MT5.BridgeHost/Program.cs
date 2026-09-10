using GoldAiTrader.MT5.BridgeHost;

var builder = WebApplication.CreateBuilder(args);
var settings = MT5BridgeHostSettings.FromConfiguration(builder.Configuration);
var app = MT5BridgeHostApplication.Build(builder, settings);
await app.RunAsync();

public partial class Program;
