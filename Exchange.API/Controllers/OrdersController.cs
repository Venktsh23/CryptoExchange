using Microsoft.AspNetCore.Mvc;
using Exchange.Core.Engine;
using Exchange.Core.Models;
using Exchange.Core.Kafka;
using Exchange.Core.Accounts;
using Exchange.Core.RateLimit;

namespace Exchange.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderProducer   _orderProducer;
    private readonly MatchingEngine  _engine;
    private readonly AccountService  _accountService;

    private readonly KafkaSettings   _kafkaSettings;

    private readonly RedisRateLimiter _rateLimiter;

    private readonly RateLimitSettings _rateLimitSettings;


    public OrdersController(
        OrderProducer  orderProducer,
        MatchingEngine engine,
        AccountService accountService,
        KafkaSettings kafkaSettings,
            RedisRateLimiter  rateLimiter,
            RateLimitSettings rateLimitSettings)
    {
        _orderProducer  = orderProducer;
        _engine         = engine;
        _accountService = accountService;
        _kafkaSettings  = kafkaSettings;
        _rateLimiter    = rateLimiter;
        _rateLimitSettings = rateLimitSettings;

    }

[HttpPost]
public async Task<IActionResult> PlaceOrder(
    [FromBody] PlaceOrderRequest request)
{
    if (request.Price <= 0 || request.Quantity <= 0)
        return BadRequest("Price and quantity must be greater than zero.");

    if (string.IsNullOrWhiteSpace(request.TradingPair))
        return BadRequest("Trading pair required.");

    if (!request.TradingPair.Contains('/'))
        return BadRequest("Invalid format. Use BASE/QUOTE e.g. BTC/USD");

    // Rate limit check — FIRST before anything else
    // Cheapest possible check — Redis only, no DB
    var rateLimitResult = await _rateLimiter.IsAllowedAsync(request.UserId);

    if (!rateLimitResult.IsAllowed)
    {
        Response.Headers["X-RateLimit-Limit"]     = _rateLimitSettings.OrdersPerSecond.ToString();
        Response.Headers["X-RateLimit-Remaining"] = "0";
        Response.Headers["Retry-After"]           = (rateLimitResult.RetryAfterMs / 1000.0).ToString("F2");

        return StatusCode(429, new
        {
            error        = "Rate limit exceeded.",
            limit        = _rateLimitSettings.OrdersPerSecond,
            windowMs     = _rateLimitSettings.WindowMs,
            retryAfterMs = rateLimitResult.RetryAfterMs
        });
    }

    // Add remaining headers for allowed requests
    Response.Headers["X-RateLimit-Limit"]     = _rateLimitSettings.OrdersPerSecond.ToString();
    Response.Headers["X-RateLimit-Remaining"] =
        (_rateLimitSettings.OrdersPerSecond - rateLimitResult.CurrentCount).ToString();

    var order = new Order
    {
        UserId      = request.UserId,
        TradingPair = request.TradingPair.ToUpper(),
        Side        = request.Side,
        Price       = request.Price,
        Quantity    = request.Quantity
    };

    var orderMessage = new Exchange.Core.Kafka.OrderMessage
    {
        Id          = order.Id,
        UserId      = order.UserId,
        TradingPair = order.TradingPair,
        Side        = order.Side.ToString(),
        Price       = order.Price,
        Quantity    = order.Quantity,
        CreatedAt   = order.CreatedAt
    };

    var orderPayload = System.Text.Json.JsonSerializer.Serialize(orderMessage);

    var validationError = await _accountService.ValidateAndLockAsync(
        orderId:      order.Id,
        userId:       order.UserId,
        tradingPair:  order.TradingPair,
        side:         order.Side.ToString(),
        price:        order.Price,
        quantity:     order.Quantity,
        orderPayload: orderPayload,
        kafkaTopic:   _kafkaSettings.OrdersTopic
    );

    if (validationError != null)
        return BadRequest(new { error = validationError });

    await _orderProducer.PublishOrderAsync(order);

    return Accepted(new
    {
        orderId          = order.Id,
        message          = "Order accepted.",
        tradingPair      = order.TradingPair,
        side             = order.Side.ToString(),
        price            = order.Price,
        quantity         = order.Quantity,
        rateLimitRemaining = _rateLimitSettings.OrdersPerSecond - rateLimitResult.CurrentCount
    });
}

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