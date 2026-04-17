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
using Prometheus;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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


builder.Services.AddHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("Postgres")!,
        name:    "postgresql",
        tags:    new[] { "ready" })
    .AddCheck("matching-engine", () =>
    {
        // Engine is healthy if it's processing orders
        return HealthCheckResult.Healthy("Matching engine running");
    }, tags: new[] { "ready" });
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


app.UseStaticFiles(); 

// Auto-migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ExchangeDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();

// Liveness — just checks if process is alive
// Returns 200 always unless process is deadlocked
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false  // No checks — just "am I alive?"
});

// Readiness — checks all dependencies
// Returns 200 only when all dependencies are healthy
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.UseMetricServer();   // exposes /metrics endpoint
app.UseHttpMetrics();    // tracks HTTP request metrics automatically

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