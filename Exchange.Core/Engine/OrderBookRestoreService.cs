using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Exchange.Core.Persistence.Repositories;

namespace Exchange.Core.Engine;

public class OrderBookRestoreService : IHostedService
{
    private readonly MatchingEngine                      _engine;
    private readonly IServiceScopeFactory                _scopeFactory;
    private readonly ILogger<OrderBookRestoreService>    _logger;

    public OrderBookRestoreService(
        MatchingEngine engine,
        IServiceScopeFactory scopeFactory,
        ILogger<OrderBookRestoreService> logger)
    {
        _engine       = engine;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    // Runs to COMPLETION before app accepts any requests
    // IHostedService.StartAsync vs BackgroundService.ExecuteAsync
    // This distinction is critical — restore must finish before
    // the Order Consumer starts processing new orders
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Order book restore starting — checking for snapshots...");

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<SnapshotRepository>();

        var pairs = await repo.GetSnapshotTradingPairsAsync();

        if (!pairs.Any())
        {
            _logger.LogInformation(
                "No snapshots found — starting with empty order books.");
            return;
        }

        var restoredPairs  = 0;
        var restoredOrders = 0;

        foreach (var pair in pairs)
        {
            var snapshot = await repo.GetLatestSnapshotAsync(pair);

            if (snapshot == null)
            {
                _logger.LogWarning(
                    "Snapshot for {Pair} could not be read — skipping.", pair);
                continue;
            }

            _engine.RestoreFromSnapshot(
                snapshot.TradingPair,
                snapshot.Bids,
                snapshot.Asks);

            var count = snapshot.Bids.Count + snapshot.Asks.Count;
            restoredOrders += count;
            restoredPairs++;

            _logger.LogInformation(
                "RESTORED | {Pair} | {Count} resting orders",
                pair, count);
        }

        _logger.LogInformation(
            "Restore complete. Pairs: {Pairs} | Orders: {Orders}",
            restoredPairs, restoredOrders);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}