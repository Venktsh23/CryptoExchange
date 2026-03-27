using Exchange.Core.Engine;
using Exchange.Core.Models;
using Exchange.Core.Persistence;
using Exchange.Core.Persistence.Repositories;
using Exchange.API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

// Configure Serilog before anything else
// If the app crashes during startup, we still get logs
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/exchange-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: 30   // Keep 30 days of logs
    )
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog(); // Replace default logging with Serilog

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
builder.Services.AddHostedService<SnapshotService>();

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