using Exchange.Core.Engine;
using Exchange.Core.Models;

Console.WriteLine("=== Crypto Exchange - Phase 1 Test Runner ===\n");

var engine = new MatchingEngine();
var random = new Random(42); // Fixed seed = reproducible results
var totalTrades = 0;
var stopwatch = System.Diagnostics.Stopwatch.StartNew();

// ─────────────────────────────────────────────
// TEST 1 — Basic match (the 1.5 BTC scenario)
// ─────────────────────────────────────────────
Console.WriteLine("TEST 1: Basic partial fill scenario");
Console.WriteLine("────────────────────────────────────");

var seller = new Order
{
    UserId      = "seller-1",
    TradingPair = "BTC/USD",
    Side        = OrderSide.Sell,
    Price       = 60_000m,
    Quantity    = 0.8m
};

var buyer = new Order
{
    UserId      = "buyer-1",
    TradingPair = "BTC/USD",
    Side        = OrderSide.Buy,
    Price       = 60_000m,
    Quantity    = 1.5m
};

// Seller rests in book first (no buyers yet)
var trades1 = engine.ProcessOrder(seller);
Console.WriteLine($"Seller placed:  0.8 BTC @ $60,000 | Trades: {trades1.Count}");

// Buyer arrives — partial match
var trades2 = engine.ProcessOrder(buyer);
Console.WriteLine($"Buyer placed:   1.5 BTC @ $60,000 | Trades: {trades2.Count}");

foreach (var t in trades2)
    Console.WriteLine($"  → Trade: {t.Quantity} BTC @ ${t.Price:N0} = ${t.TotalValue:N0}");

var book1 = engine.GetOrderBook("BTC/USD")!;
Console.WriteLine($"Buyer remaining in book: {book1.BestBid} bid with " +
    $"{book1.Bids.Values.First().Peek().RemainingQuantity} BTC\n");

// ─────────────────────────────────────────────
// TEST 2 — Better price scenario
// ─────────────────────────────────────────────
Console.WriteLine("TEST 2: Buyer gets better price than limit");
Console.WriteLine("────────────────────────────────────────────");

var engine2 = new MatchingEngine();

var cheapSeller = new Order
{
    UserId      = "seller-2",
    TradingPair = "ETH/USD",
    Side        = OrderSide.Sell,
    Price       = 3_200m,   // Seller wants $3,200
    Quantity    = 1m
};

var richBuyer = new Order
{
    UserId      = "buyer-2",
    TradingPair = "ETH/USD",
    Side        = OrderSide.Buy,
    Price       = 3_500m,   // Buyer willing to pay up to $3,500
    Quantity    = 1m
};

engine2.ProcessOrder(cheapSeller);
var trades3 = engine2.ProcessOrder(richBuyer);

Console.WriteLine($"Seller wants:   $3,200 per ETH");
Console.WriteLine($"Buyer limit:    $3,500 per ETH");
foreach (var t in trades3)
    Console.WriteLine($"  → Executed at: ${t.Price:N0} (buyer saved ${richBuyer.Price - t.Price:N0})\n");

// ─────────────────────────────────────────────
// TEST 3 — 10,000 orders throughput test
// ─────────────────────────────────────────────
Console.WriteLine("TEST 3: 10,000 orders throughput");
Console.WriteLine("──────────────────────────────────");

var engine3  = new MatchingEngine();
var tradeCount = 0;

// Prices fluctuate between $59,000 and $61,000
// Buyers and sellers randomly appear — realistic simulation
for (int i = 0; i < 10_000; i++)
{
    var side  = random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;

    // Buyers bid between $59,500–$60,500
    // Sellers ask between $59,800–$60,800
    // Overlap exists — trades WILL happen
    var price = side == OrderSide.Buy
        ? Math.Round(59_500m + (decimal)random.NextDouble() * 1_000m, 2)
        : Math.Round(59_800m + (decimal)random.NextDouble() * 1_000m, 2);

    var qty = Math.Round(0.01m + (decimal)random.NextDouble() * 2m, 4);

    var order = new Order
    {
        UserId      = $"user-{random.Next(1, 200)}",
        TradingPair = "BTC/USD",
        Side        = side,
        Price       = price,
        Quantity    = qty
    };

    var trades = engine3.ProcessOrder(order);
    tradeCount += trades.Count;
}

stopwatch.Stop();

var (restingOrders, executedTrades) = engine3.GetStats("BTC/USD");

Console.WriteLine($"Orders processed : 10,000");
Console.WriteLine($"Trades executed  : {tradeCount:N0}");
Console.WriteLine($"Resting in book  : {restingOrders:N0}");
Console.WriteLine($"Total time       : {stopwatch.ElapsedMilliseconds}ms");
Console.WriteLine($"Avg per order    : {stopwatch.Elapsed.TotalMicroseconds / 10_000:F2} microseconds");

var microsPerOrder = stopwatch.Elapsed.TotalMicroseconds / 10_000;

if (microsPerOrder < 50)
    Console.WriteLine($"\n✓ PASSED — {microsPerOrder:F2}μs per order (sequential benchmark)");
else
    Console.WriteLine($"\n✗ Review — {microsPerOrder:F2}μs per order, check for bottlenecks");