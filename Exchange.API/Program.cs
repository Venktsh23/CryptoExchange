using Exchange.Core.Engine;
using Exchange.Core.Models;
using Exchange.Core.Persistence;
using Exchange.Core.Persistence.Repositories;
using Exchange.API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// PostgreSQL — connection string from config
builder.Services.AddDbContext<ExchangeDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres")
    )
);

// Repository — scoped because DbContext is scoped
builder.Services.AddScoped<TradeRepository>();

// Engine services — singletons
builder.Services.AddSingleton<MatchingEngine>();
builder.Services.AddSingleton<OrderChannel>();
builder.Services.AddSingleton<SettlementChannel>();
builder.Services.AddSingleton<MatchingEngineService>();

// Hosted services
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<MatchingEngineService>());
builder.Services.AddHostedService<SettlementWorker>();

var app = builder.Build();

// Auto-create database tables on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ExchangeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();

// Wire SignalR to engine
var engineService = app.Services.GetRequiredService<MatchingEngineService>();
var hubContext    = app.Services.GetRequiredService<IHubContext<MarketDataHub>>();

engineService.SetTradeCallback(async (Trade trade) =>
{
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
app.MapHub<MarketDataHub>("/hubs/marketdata");

app.Run();