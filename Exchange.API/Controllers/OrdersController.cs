using Microsoft.AspNetCore.Mvc;
using Exchange.Core.Engine;
using Exchange.Core.Models;
using Exchange.Core.Persistence.Repositories;

namespace Exchange.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderChannel _orderChannel;
    private readonly MatchingEngine _engine;

    public OrdersController(OrderChannel orderChannel, MatchingEngine engine)
    {
        _orderChannel = orderChannel;
        _engine = engine;
    }

    // POST api/orders
    // Receives an order, drops it on the channel, returns immediately
    [HttpPost]
    public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest request)
    {
        // Basic validation
        if (request.Price <= 0 || request.Quantity <= 0)
            return BadRequest("Price and quantity must be greater than zero.");

        if (string.IsNullOrWhiteSpace(request.TradingPair))
            return BadRequest("Trading pair is required. e.g. BTC/USD");

        var order = new Order
        {
            UserId      = request.UserId,
            TradingPair = request.TradingPair.ToUpper(),
            Side        = request.Side,
            Price       = request.Price,
            Quantity    = request.Quantity
        };

        // Drop on the belt — returns immediately, does NOT wait for matching
        await _orderChannel.Writer.WriteAsync(order);

        return Accepted(new
        {
            orderId     = order.Id,
            message     = "Order received and queued for matching.",
            tradingPair = order.TradingPair,
            side        = order.Side.ToString(),
            price       = order.Price,
            quantity    = order.Quantity
        });
    }

    // GET api/orders/book/BTC%2FUSD
    // Returns current state of the order book for a trading pair
    [HttpGet("book/{tradingPair}")]
    public IActionResult GetOrderBook(string tradingPair)
    {
        var pair = Uri.UnescapeDataString(tradingPair).ToUpper();
        var book = _engine.GetOrderBook(pair);

        if (book == null)
            return NotFound($"No order book found for {pair}");

        // Shape the response to be readable
        var response = new
        {
            tradingPair = book.TradingPair,
            bestBid     = book.BestBid,
            bestAsk     = book.BestAsk,
            spread      = book.Spread,
            bids        = book.Bids.Take(10).Select(level => new
            {
                price    = level.Key,
                quantity = level.Value.Sum(o => o.RemainingQuantity),
                orders   = level.Value.Count
            }),
            asks        = book.Asks.Take(10).Select(level => new
            {
                price    = level.Key,
                quantity = level.Value.Sum(o => o.RemainingQuantity),
                orders   = level.Value.Count
            })
        };

        return Ok(response);
    }

    // GET api/orders/stats/BTC%2FUSD
    [HttpGet("stats/{tradingPair}")]
    public IActionResult GetStats(string tradingPair)
    {
        var pair = Uri.UnescapeDataString(tradingPair).ToUpper();
        var (totalOrders, totalTrades) = _engine.GetStats(pair);

        return Ok(new
        {
            tradingPair  = pair,
            restingOrders = totalOrders,
            tradesExecuted = totalTrades
        });
    }

    // GET api/orders/trades/BTC%2FUSD
[HttpGet("trades/{tradingPair}")]
public async Task<IActionResult> GetRecentTrades(
    string tradingPair,
    [FromServices] TradeRepository repo)
{
    var pair   = Uri.UnescapeDataString(tradingPair).ToUpper();
    var trades = await repo.GetRecentTradesAsync(pair, 50);

    return Ok(trades.Select(t => new
    {
        t.Id,
        t.TradingPair,
        t.Price,
        t.Quantity,
        t.TotalValue,
        t.BuyerUserId,
        t.SellerUserId,
        t.ExecutedAt,
        t.PersistedAt,
        latencyMs = (t.PersistedAt - t.ExecutedAt).TotalMilliseconds
    }));
}
}

// The shape of the incoming HTTP request body
public record PlaceOrderRequest(
    string UserId,
    string TradingPair,
    OrderSide Side,
    decimal Price,
    decimal Quantity
);