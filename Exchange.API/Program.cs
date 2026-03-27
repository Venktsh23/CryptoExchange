using Exchange.Core.Engine;
using Exchange.Core.Models;
using Exchange.API.Hubs;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Add SignalR
builder.Services.AddSignalR();

// Allow the browser HTML file to connect
// (since it opens from file://, not localhost)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true)  // Allow any origin in dev
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();            // Required for SignalR
    });
});

builder.Services.AddSingleton<MatchingEngine>();
builder.Services.AddSingleton<OrderChannel>();
builder.Services.AddSingleton<MatchingEngineService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchingEngineService>());

var app = builder.Build();

app.UseCors();

// Wire up the SignalR broadcast to the engine
// This runs once at startup — connects the engine to the hub
var engineService = app.Services.GetRequiredService<MatchingEngineService>();
var hubContext    = app.Services.GetRequiredService<IHubContext<MarketDataHub>>();

engineService.SetTradeCallback(async (Trade trade) =>
{
    // Broadcast to everyone in the trading pair group
    // e.g. all subscribers of "BTC/USD" get this message
    await hubContext.Clients
        .Group(trade.TradingPair)
        .SendAsync("TradeExecuted", new
        {
            tradingPair  = trade.TradingPair,
            price        = trade.Price,
            quantity     = trade.Quantity,
            totalValue   = trade.TotalValue,
            buyerUserId  = trade.BuyerUserId,
            sellerUserId = trade.SellerUserId,
            executedAt   = trade.ExecutedAt
        });

    // Also broadcast latest price to pair's price feed
    await hubContext.Clients
        .Group(trade.TradingPair)
        .SendAsync("PriceUpdated", new
        {
            tradingPair = trade.TradingPair,
            price       = trade.Price,
            timestamp   = trade.ExecutedAt
        });
});

app.MapControllers();

// Map the SignalR hub to a URL endpoint
app.MapHub<MarketDataHub>("/hubs/marketdata");

app.Run();