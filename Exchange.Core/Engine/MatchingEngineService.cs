using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Exchange.Core.Models;

namespace Exchange.Core.Engine;

// BackgroundService = a .NET built-in that runs a loop
// for the entire lifetime of your application
public class MatchingEngineService : BackgroundService
{
    private readonly MatchingEngine _engine;
    private readonly OrderChannel _orderChannel;
    private readonly ILogger<MatchingEngineService> _logger;

    // Tracks total trades and processing time for stats
    private long _totalProcessed = 0;
    private long _totalTrades = 0;

    public MatchingEngineService(
        MatchingEngine engine,
        OrderChannel orderChannel,
        ILogger<MatchingEngineService> logger)
    {
        _engine = engine;
        _orderChannel = orderChannel;
        _logger = logger;
    }

    // This method runs from app start until app shutdown
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Matching Engine started. Waiting for orders...");

        // ReadAllAsync — blocks efficiently when channel is empty
        // Wakes up instantly when an order arrives
        // Stops cleanly when the app shuts down (stoppingToken)
        await foreach (var order in _orderChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var started = DateTime.UtcNow;

                var trades = _engine.ProcessOrder(order);

                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

                Interlocked.Increment(ref _totalProcessed);
                Interlocked.Add(ref _totalTrades, trades.Count);

                if (trades.Count > 0)
                {
                    foreach (var trade in trades)
                    {
                        _logger.LogInformation(
                            "TRADE EXECUTED | Pair: {Pair} | Price: {Price:C} | " +
                            "Qty: {Qty} | Value: {Value:C} | Buyer: {Buyer} | Seller: {Seller}",
                            trade.TradingPair,
                            trade.Price,
                            trade.Quantity,
                            trade.TotalValue,
                            trade.BuyerUserId,
                            trade.SellerUserId
                        );
                    }
                }

                // Log every 1000 orders to track throughput
                if (_totalProcessed % 1000 == 0)
                {
                    _logger.LogInformation(
                        "ENGINE STATS | Orders processed: {Orders} | " +
                        "Trades executed: {Trades} | Last order took: {Ms:F3}ms",
                        _totalProcessed, _totalTrades, elapsed
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing order {OrderId}", order.Id);
            }
        }

        _logger.LogInformation("Matching Engine stopped.");
    }

    public (long orders, long trades) GetStats() => (_totalProcessed, _totalTrades);
}