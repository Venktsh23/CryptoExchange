using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Exchange.Core.Models;

namespace Exchange.Core.Engine;

// We use an Action callback instead of IHubContext directly
// because Exchange.Core has no reference to SignalR
// The API project will inject the broadcast function
public class MatchingEngineService : BackgroundService
{
    private readonly MatchingEngine _engine;
    private readonly OrderChannel _orderChannel;
    private readonly ILogger<MatchingEngineService> _logger;

    private readonly SettlementChannel _settlementChannel;

    // This is the broadcast callback — the API injects this
    // when it registers the service
    private Func<Trade, Task>? _onTradeExecuted;

    private long _totalProcessed = 0;
    private long _totalTrades = 0;

    public MatchingEngineService(
        MatchingEngine engine,
        OrderChannel orderChannel,
            SettlementChannel settlementChannel, 
        ILogger<MatchingEngineService> logger)
    {
        _engine = engine;
        _orderChannel = orderChannel;
        _settlementChannel = settlementChannel;
        _logger = logger;
    }

    // The API calls this once at startup to wire up broadcasting
    public void SetTradeCallback(Func<Trade, Task> callback)
    {
        _onTradeExecuted = callback;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Matching Engine started. Waiting for orders...");

        await foreach (var order in _orderChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var started = DateTime.UtcNow;
                var trades = _engine.ProcessOrder(order);
                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

                Interlocked.Increment(ref _totalProcessed);
                Interlocked.Add(ref _totalTrades, trades.Count);

                // Broadcast each trade the moment it executes
               foreach (var trade in trades)
{
    _logger.LogInformation(
        "TRADE | {Pair} | {Qty} @ {Price:C} | Buyer: {Buyer} | Seller: {Seller}",
        trade.TradingPair, trade.Quantity, trade.Price,
        trade.BuyerUserId, trade.SellerUserId
    );

    // 1. Push to SignalR — fire and forget, engine doesn't wait
    if (_onTradeExecuted != null)
        _ = Task.Run(() => _onTradeExecuted(trade), stoppingToken);

    // 2. Push to settlement channel — engine doesn't wait for DB
    await _settlementChannel.Writer.WriteAsync(trade, stoppingToken);
}

                if (_totalProcessed % 1000 == 0)
                {
                    _logger.LogInformation(
                        "ENGINE STATS | Processed: {Orders} | " +
                        "Trades: {Trades} | Last: {Ms:F3}ms",
                        _totalProcessed, _totalTrades, elapsed
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing order {OrderId}", order.Id);
            }
        }

        _logger.LogInformation("Matching Engine stopped.");
    }

    public (long orders, long trades) GetStats() => (_totalProcessed, _totalTrades);
}