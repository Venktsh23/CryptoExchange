using Microsoft.AspNetCore.Mvc;
using Exchange.Core.Engine;
using Exchange.Core.Models;
using Exchange.Core.Kafka;
using Exchange.Core.Accounts;

namespace Exchange.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderProducer   _orderProducer;
    private readonly MatchingEngine  _engine;
    private readonly AccountService  _accountService;

    public OrdersController(
        OrderProducer  orderProducer,
        MatchingEngine engine,
        AccountService accountService)
    {
        _orderProducer  = orderProducer;
        _engine         = engine;
        _accountService = accountService;
    }

    [HttpPost]
    public async Task<IActionResult> PlaceOrder(
        [FromBody] PlaceOrderRequest request)
    {
        if (request.Price <= 0 || request.Quantity <= 0)
            return BadRequest("Price and quantity must be greater than zero.");

        if (string.IsNullOrWhiteSpace(request.TradingPair))
            return BadRequest("Trading pair required. e.g. BTC/USD");

        if (!request.TradingPair.Contains('/'))
            return BadRequest("Invalid trading pair format. Use BASE/QUOTE e.g. BTC/USD");

        var order = new Order
        {
            UserId      = request.UserId,
            TradingPair = request.TradingPair.ToUpper(),
            Side        = request.Side,
            Price       = request.Price,
            Quantity    = request.Quantity
        };

        // Validate balance and lock funds before accepting order
        var validationError = await _accountService.ValidateAndLockAsync(
            orderId:     order.Id,
            userId:      order.UserId,
            tradingPair: order.TradingPair,
            side:        order.Side.ToString(),
            price:       order.Price,
            quantity:    order.Quantity
        );

        if (validationError != null)
            return BadRequest(new { error = validationError });

        // Funds locked — now safe to publish to Kafka
        await _orderProducer.PublishOrderAsync(order);

        return Accepted(new
        {
            orderId     = order.Id,
            message     = "Order accepted. Funds locked. Queued for matching.",
            tradingPair = order.TradingPair,
            side        = order.Side.ToString(),
            price       = order.Price,
            quantity    = order.Quantity
        });
    }

    // Existing endpoints unchanged below
    [HttpGet("book/{tradingPair}")]
    public IActionResult GetOrderBook(string tradingPair)
    {
        var pair = Uri.UnescapeDataString(tradingPair).ToUpper();
        var book = _engine.GetOrderBook(pair);
        if (book == null) return NotFound($"No order book for {pair}");

        return Ok(new
        {
            tradingPair = book.TradingPair,
            bestBid     = book.BestBid,
            bestAsk     = book.BestAsk,
            spread      = book.Spread,
            bids = book.Bids.Take(10).Select(l => new
            {
                price    = l.Key,
                quantity = l.Value.Sum(o => o.RemainingQuantity),
                orders   = l.Value.Count
            }),
            asks = book.Asks.Take(10).Select(l => new
            {
                price    = l.Key,
                quantity = l.Value.Sum(o => o.RemainingQuantity),
                orders   = l.Value.Count
            })
        });
    }

    [HttpGet("stats/{tradingPair}")]
    public IActionResult GetStats(string tradingPair)
    {
        var pair = Uri.UnescapeDataString(tradingPair).ToUpper();
        var (orders, trades) = _engine.GetStats(pair);
        return Ok(new { tradingPair = pair, orders, trades });
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