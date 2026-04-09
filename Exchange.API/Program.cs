using Serilog;
using Serilog.Events;
using Exchange.Core.Engine;
using Exchange.Core.Models;
using Exchange.Core.Kafka;
using Exchange.Core.Persistence;
using Exchange.Core.Persistence.Repositories;
using Exchange.API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Exchange.Core.Accounts;
using StackExchange.Redis;
using Exchange.Core.RateLimit;

// Serilog setup
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override(
        "Microsoft.EntityFrameworkCore.Database.Command",
        LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/exchange-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(_ => true)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// PostgreSQL
builder.Services.AddDbContext<ExchangeDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddScoped<TradeRepository>();
builder.Services.AddScoped<SnapshotRepository>();
builder.Services.AddScoped<AccountRepository>();
builder.Services.AddScoped<AccountService>();

// Kafka settings — read from appsettings.json
var kafkaSettings = builder.Configuration
    .GetSection("Kafka")
    .Get<KafkaSettings>() ?? new KafkaSettings();

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis")!));


// Rate limit settings
var rateLimitSettings = builder.Configuration
    .GetSection("RateLimit")
    .Get<RateLimitSettings>() ?? new RateLimitSettings();

builder.Services.AddSingleton(rateLimitSettings);
builder.Services.AddSingleton<RedisRateLimiter>();



builder.Services.AddSingleton(kafkaSettings);

// Kafka producers
builder.Services.AddSingleton<OrderProducer>();
builder.Services.AddSingleton<TradeProducer>();

// Engine + channels
builder.Services.AddSingleton<MatchingEngine>();
builder.Services.AddSingleton<SettlementChannel>();

// Hosted services
builder.Services.AddSingleton<OrderConsumerService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<OrderConsumerService>());

builder.Services.AddHostedService<KafkaSettlementWorker>();
builder.Services.AddHostedService<SnapshotService>();
builder.Services.AddHostedService<OrderBookRestoreService>();
builder.Services.AddHostedService<OutboxPublisherService>();

var app = builder.Build();

// Auto-migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ExchangeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();

// Wire SignalR broadcast to the order consumer
var orderConsumer = app.Services
    .GetRequiredService<OrderConsumerService>();
var hubContext    = app.Services
    .GetRequiredService<IHubContext<MarketDataHub>>();

orderConsumer.SetTradeCallback(async (Trade trade) =>
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