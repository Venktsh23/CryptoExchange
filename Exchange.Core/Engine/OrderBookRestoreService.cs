using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Exchange.Core.Persistence.Repositories;
using Exchange.Core.Models;

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
        var tradeRepo = scope.ServiceProvider
            .GetRequiredService<TradeRepository>();

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
            // 1 safety net — matching engine removes filled orders, the below is added as a safety net incase maching engine was 
            // stopped before it could remove filled orders from the order book. This ensures that we don't restore 
            // orders that have already been filled.
            var pendingBids = snapshot.Bids.Where(b => 
            b.Status is OrderStatus.Pending or OrderStatus.PartiallyFilled).ToList();
            var pendingAsks = snapshot.Asks.Where(a => 
            a.Status is OrderStatus.Pending or OrderStatus.PartiallyFilled).ToList();


            var allOrderIds = pendingBids.Concat(pendingAsks)
                .Select(a => a.Id)
                .ToHashSet();   

            var ghostCount = 0;
            var partialCount = 0;

            if (allOrderIds.Count > 0)
            {
                var filledQuantities = await tradeRepo.GetFilledQuantitiesAsync(allOrderIds);
                pendingBids = Recouncil(pendingBids, filledQuantities, ref ghostCount, ref partialCount);
                pendingAsks = Recouncil(pendingAsks, filledQuantities, ref ghostCount, ref partialCount);
            }

            if(ghostCount > 0 || partialCount > 0)
            {
                _logger.LogInformation(
                    "Reconciled {Pair} | Ghosts removed: {Ghosts} | Partials updated: {Partials}",
                    pair, ghostCount, partialCount);
            }

            _engine.RestoreFromSnapshot(
                snapshot.TradingPair,
                pendingBids,
                pendingAsks);

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

    // Reconcile snapshot orders against trade table
    //three outcomes for each order:
    //1. No trades — keep as is
    //2. Fully filled — remove from order book (ghost)
    //3. Partially filled — -restore with updated filled quantity (partial)
    private static List<Order> Recouncil(
        List<Order> orders,
         Dictionary<Guid, decimal> filledQuantities,
         ref int ghostCount, ref int partialCount)
    {
        var reconciled = new List<Order>();

        foreach (var order in orders)
        {
           if(!filledQuantities.TryGetValue(order.Id, out var filled))
           {
                reconciled.Add(order);// no trades — keep as is
                continue;
           }
           var remaining = order.Quantity - filled;
              if (remaining <= 0)
              {
                ghostCount++;
                continue; // fully filled — skip
              }

              order.FilledQuantity = filled;
                order.Status = OrderStatus.PartiallyFilled; // partially filled — update status and quantity
                partialCount++;
                reconciled.Add(order);
                       
        }

        return reconciled;
    }
    

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}