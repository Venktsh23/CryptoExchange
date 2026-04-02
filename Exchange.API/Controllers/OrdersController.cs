using Microsoft.AspNetCore.Mvc;
using Exchange.Core.Engine;
using Exchange.Core.Models;
using Exchange.Core.Kafka;

namespace Exchange.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderProducer  _orderProducer;
    private readonly MatchingEngine _engine;

    public OrdersController(
        OrderProducer orderProducer,
        MatchingEngine engine)
    {
        _orderProducer = orderProducer;
        _engine        = engine;
    }

    // POST api/orders
    // Receives an order, publishes it to kafka topic, returns immediately
    [HttpPost]
    public async Task<IActionResult> PlaceOrder(
        [FromBody] PlaceOrderRequest request)
    {
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

        // Publish to Kafka — durable, survives crashes
        // Returns as soon as Kafka confirms receipt
        await _orderProducer.PublishOrderAsync(order);

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

    [HttpGet("book/{tradingPair}")]
    public IActionResult GetOrderBook(string tradingPair)
    {
        var pair = Uri.UnescapeDataString(tradingPair).ToUpper();
        var book = _engine.GetOrderBook(pair);

        if (book == null)
            return NotFound($"No order book found for {pair}");

        return Ok(new
        {
            tradingPair = book.TradingPair,
            bestBid     = book.BestBid,
            bestAsk     = book.BestAsk,
            spread      = book.Spread,
            bids = book.Bids.Take(10).Select(level => new
            {
                price    = level.Key,
                quantity = level.Value.Sum(o => o.RemainingQuantity),
                orders   = level.Value.Count
            }),
            asks = book.Asks.Take(10).Select(level => new
            {
                price    = level.Key,
                quantity = level.Value.Sum(o => o.RemainingQuantity),
                orders   = level.Value.Count
            })
        });
    }

    [HttpGet("stats/{tradingPair}")]
    public IActionResult GetStats(string tradingPair)
    {
        var pair = Uri.UnescapeDataString(tradingPair).ToUpper();
        var (totalOrders, totalTrades) = _engine.GetStats(pair);
        return Ok(new { tradingPair = pair, totalOrders, totalTrades });
    }

    [HttpGet("trades/{tradingPair}")]
    public async Task<IActionResult> GetRecentTrades(
        string tradingPair,
        [FromServices] Exchange.Core.Persistence.Repositories.TradeRepository repo)
    {
        var pair   = Uri.UnescapeDataString(tradingPair).ToUpper();
        var trades = await repo.GetRecentTradesAsync(pair, 50);
        return Ok(trades);
    }
}

public record PlaceOrderRequest(
    string    UserId,
    string    TradingPair,
    OrderSide Side,
    decimal   Price,
    decimal   Quantity
);