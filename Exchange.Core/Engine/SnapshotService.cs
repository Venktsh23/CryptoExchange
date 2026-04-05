using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Exchange.Core.Models;
using Exchange.Core.Persistence;
using Exchange.Core.Persistence.Entities;

namespace Exchange.Core.Engine;

public class SnapshotService : BackgroundService
{
    private readonly MatchingEngine _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SnapshotService> _logger;

    // Snapshot every 5 minutes
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);

    public SnapshotService(
        MatchingEngine engine,
        IServiceScopeFactory scopeFactory,
        ILogger<SnapshotService> logger)
    {
        _engine      = engine;
        _scopeFactory = scopeFactory;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Snapshot Service started. Saving order book every {Minutes} minutes.",
            _interval.TotalMinutes
        );
        try
        {
            // Wait for first interval before first snapshot
        await Task.Delay(_interval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TakeSnapshotsAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Snapshot failed — will retry next interval");
            }

            await Task.Delay(_interval, stoppingToken);
        }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize snapshot service.");
        }
        finally
        {
            _logger.LogInformation("Snapshot Service is stopping.");
            try
            {
                await TakeSnapshotsAsync(CancellationToken.None);
                _logger.LogInformation("Final snapshot taken during shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final snapshot failed during shutdown.");
            }
        }

        
    }

    private async Task TakeSnapshotsAsync(CancellationToken ct)
    {
        // Get all active trading pairs from the engine
        var tradingPairs = _engine.GetActiveTradingPairs();

        if (!tradingPairs.Any())
        {
            _logger.LogInformation("Snapshot: No active order books to snapshot.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExchangeDbContext>();

        foreach (var pair in tradingPairs)
        {
            var book = _engine.GetOrderBook(pair);
            if (book == null) continue;

            // Serialize the order book to JSON
            var snapshotData = SerializeOrderBook(book);
            var restingCount = book.Bids.Values.Sum(q => q.Count)
                             + book.Asks.Values.Sum(q => q.Count);

            var snapshot = new OrderBookSnapshotEntity
            {
                TradingPair       = pair,
                SnapshotJson      = JsonSerializer.Serialize(snapshotData),
                CreatedAt         = DateTime.UtcNow,
                RestingOrderCount = restingCount
            };

            db.OrderBookSnapshots.Add(snapshot);

            _logger.LogInformation(
                "SNAPSHOT | {Pair} | {Count} resting orders captured",
                pair, restingCount
            );

            // Prune old snapshots — keep only latest 12 per pair
            var old = await db.OrderBookSnapshots
                .Where(s => s.TradingPair == pair)
                .OrderByDescending(s => s.CreatedAt)
                .Skip(12)
                .ToListAsync(ct);

            db.OrderBookSnapshots.RemoveRange(old);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Snapshot complete for {Count} pairs.", tradingPairs.Count());
    }

    // Converts the order book into a serializable structure
  private static object SerializeOrderBook(OrderBook book)
    {
        return new
        {
            tradingPair = book.TradingPair,
            bids = book.Bids.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value
                    .Where(o => o.RemainingQuantity > 0 &&
                                o.Status != OrderStatus.Filled &&
                                o.Status != OrderStatus.Cancelled)
                    .Select(o => new
                    {
                        o.Id, o.UserId, o.TradingPair,
                        Side          = o.Side.ToString(),
                        o.Price, o.Quantity,
                        o.FilledQuantity,
                        Status        = o.Status.ToString(),
                        o.CreatedAt
                    }).ToList()
            ),
            asks = book.Asks.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value
                    .Where(o => o.RemainingQuantity > 0 &&
                                o.Status != OrderStatus.Filled &&
                                o.Status != OrderStatus.Cancelled)
                    .Select(o => new
                    {
                        o.Id, o.UserId, o.TradingPair,
                        Side          = o.Side.ToString(),
                        o.Price, o.Quantity,
                        o.FilledQuantity,
                        Status        = o.Status.ToString(),
                        o.CreatedAt
                    }).ToList()
            )
        };
    }
}