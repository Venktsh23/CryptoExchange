using Exchange.Core.Engine;
using Exchange.Core.Models;
using FluentAssertions;

namespace Exchange.Tests;

public class MatchingEngineTests
{
    private readonly MatchingEngine _engine = new();

    // ─── Basic Matching ───────────────────────────────────

    [Fact]
    public void ExactMatch_BuyAndSell_ExecutesTrade()
    {
        var sell = MakeOrder(OrderSide.Sell, price: 60_000, qty: 1.0m);
        var buy  = MakeOrder(OrderSide.Buy,  price: 60_000, qty: 1.0m);

        _engine.ProcessOrder(sell);
        var trades = _engine.ProcessOrder(buy);

        trades.Should().HaveCount(1);
        trades[0].Price.Should().Be(60_000);
        trades[0].Quantity.Should().Be(1.0m);
        trades[0].BuyerUserId.Should().Be(buy.UserId);
        trades[0].SellerUserId.Should().Be(sell.UserId);
    }

    [Fact]
    public void BuyerPaysSellerPrice_WhenBuyLimitHigher()
    {
        // Seller wants $59,500 — buyer willing to pay $60,000
        // Trade executes at seller's price ($59,500)
        var sell = MakeOrder(OrderSide.Sell, price: 59_500, qty: 1.0m);
        var buy  = MakeOrder(OrderSide.Buy,  price: 60_000, qty: 1.0m);

        _engine.ProcessOrder(sell);
        var trades = _engine.ProcessOrder(buy);

        trades.Should().HaveCount(1);
        trades[0].Price.Should().Be(59_500);    // seller's price
        trades[0].TotalValue.Should().Be(59_500m);
    }

    [Fact]
    public void NoMatch_WhenBuyPriceLowerThanSellPrice()
    {
        // Buyer wants $59,000 — seller wants $60,000
        // No match — spread exists
        var sell = MakeOrder(OrderSide.Sell, price: 60_000, qty: 1.0m);
        var buy  = MakeOrder(OrderSide.Buy,  price: 59_000, qty: 1.0m);

        _engine.ProcessOrder(sell);
        var trades = _engine.ProcessOrder(buy);

        trades.Should().BeEmpty();
    }

    // ─── Partial Fills ────────────────────────────────────

    [Fact]
    public void PartialFill_BuyerWantsMore_SellerFullyFilled()
    {
        // Buyer wants 1.5 BTC, seller has 0.8 BTC
        var sell = MakeOrder(OrderSide.Sell, price: 60_000, qty: 0.8m);
        var buy  = MakeOrder(OrderSide.Buy,  price: 60_000, qty: 1.5m);

        _engine.ProcessOrder(sell);
        var trades = _engine.ProcessOrder(buy);

        trades.Should().HaveCount(1);
        trades[0].Quantity.Should().Be(0.8m);

        // Seller fully filled
        sell.Status.Should().Be(OrderStatus.Filled);
        sell.RemainingQuantity.Should().Be(0);

        // Buyer partially filled — 0.7 BTC remaining
        buy.Status.Should().Be(OrderStatus.PartiallyFilled);
        buy.RemainingQuantity.Should().Be(0.7m);
    }

    [Fact]
    public void PartialFill_SellerWantsMore_BuyerFullyFilled()
    {
        var sell = MakeOrder(OrderSide.Sell, price: 60_000, qty: 2.0m);
        var buy  = MakeOrder(OrderSide.Buy,  price: 60_000, qty: 1.0m);

        _engine.ProcessOrder(sell);
        var trades = _engine.ProcessOrder(buy);

        trades.Should().HaveCount(1);
        trades[0].Quantity.Should().Be(1.0m);

        buy.Status.Should().Be(OrderStatus.Filled);
        sell.Status.Should().Be(OrderStatus.PartiallyFilled);
        sell.RemainingQuantity.Should().Be(1.0m);
    }

    // ─── Multiple Fills ───────────────────────────────────

    [Fact]
    public void LargeOrder_FilledByMultipleSellers()
    {
        // Buyer wants 3.0 BTC
        // Three sellers each have 1.0 BTC
        var sell1 = MakeOrder(OrderSide.Sell, price: 60_000, qty: 1.0m);
        var sell2 = MakeOrder(OrderSide.Sell, price: 60_000, qty: 1.0m);
        var sell3 = MakeOrder(OrderSide.Sell, price: 60_000, qty: 1.0m);
        var buy   = MakeOrder(OrderSide.Buy,  price: 60_000, qty: 3.0m);

        _engine.ProcessOrder(sell1);
        _engine.ProcessOrder(sell2);
        _engine.ProcessOrder(sell3);
        var trades = _engine.ProcessOrder(buy);

        trades.Should().HaveCount(3);
        trades.Sum(t => t.Quantity).Should().Be(3.0m);
        buy.Status.Should().Be(OrderStatus.Filled);
    }

    // ─── Price Time Priority ──────────────────────────────

    [Fact]
    public void PriceTimePriority_EarlierOrderFilledFirst()
    {
        // Two sellers at same price — first one in gets filled first
        var sell1 = MakeOrder(OrderSide.Sell, price: 60_000, qty: 1.0m, userId: "seller-first");
        var sell2 = MakeOrder(OrderSide.Sell, price: 60_000, qty: 1.0m, userId: "seller-second");
        var buy   = MakeOrder(OrderSide.Buy,  price: 60_000, qty: 1.0m);

        _engine.ProcessOrder(sell1);
        _engine.ProcessOrder(sell2);
        var trades = _engine.ProcessOrder(buy);

        trades.Should().HaveCount(1);
        trades[0].SellerUserId.Should().Be("seller-first");
    }

    [Fact]
    public void PricePriority_BestPriceFilledFirst()
    {
        // Two sellers — one cheaper than the other
        // Buyer gets the cheaper price first
        var sell1 = MakeOrder(OrderSide.Sell, price: 60_100, qty: 1.0m, userId: "expensive-seller");
        var sell2 = MakeOrder(OrderSide.Sell, price: 59_900, qty: 1.0m, userId: "cheap-seller");
        var buy   = MakeOrder(OrderSide.Buy,  price: 60_500, qty: 1.0m);

        _engine.ProcessOrder(sell1);
        _engine.ProcessOrder(sell2);
        var trades = _engine.ProcessOrder(buy);

        trades.Should().HaveCount(1);
        trades[0].SellerUserId.Should().Be("cheap-seller");
        trades[0].Price.Should().Be(59_900);
    }


    // ─── Throughput ───────────────────────────────────────

    [Fact]
    public void Throughput_TenThousandOrders_CompletesUnderOneSecond()
    {
        var random    = new Random(42);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 10_000; i++)
        {
            var side  = random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
            var price = side == OrderSide.Buy
                ? Math.Round(59_500m + (decimal)random.NextDouble() * 1_000m, 2)
                : Math.Round(59_800m + (decimal)random.NextDouble() * 1_000m, 2);
            var qty = Math.Round(0.01m + (decimal)random.NextDouble() * 2m, 4);

            _engine.ProcessOrder(MakeOrder(side, price, qty, pair: "BTC/USD"));
        }

        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000,
            "engine should process 10k orders in under 1 second");
    }

    // ─── Helper ───────────────────────────────────────────

    private static Order MakeOrder(
        OrderSide side,
        decimal   price,
        decimal   qty    = 1.0m,
        string    userId = null!,
        string    pair   = "BTC/USD")
    {
        return new Order
        {
            UserId      = userId ?? (side == OrderSide.Buy ? "buyer-1" : "seller-1"),
            TradingPair = pair,
            Side        = side,
            Price       = price,
            Quantity    = qty
        };
    }
}