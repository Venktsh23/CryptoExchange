using Exchange.Core.Engine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Register as singletons — one instance shared across entire app lifetime
// The engine and channel must be shared — not recreated per request
builder.Services.AddSingleton<MatchingEngine>();
builder.Services.AddSingleton<OrderChannel>();

// BackgroundService — starts automatically, runs until app stops
builder.Services.AddHostedService<MatchingEngineService>();

var app = builder.Build();

app.MapControllers();

app.Run();