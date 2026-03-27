using Exchange.Core.Models;

namespace Exchange.Core.Engine;

public class MatchingEngine
{
    // One order book per trading pair — BTC/USD and ETH/USD are completely separate
    private readonly Dictionary<string, OrderBook> _orderBooks = new();

    // All trades that have happened — in memory for now
    private readonly List<Trade> _trades = new();

    // Lock object — only ONE thread can run matching at a time
    // This prevents two orders being matched against the same resting order
    private readonly Lock _lock = new();

    public IReadOnlyList<Trade> Trades => _trades.AsReadOnly();

    // ─────────────────────────────────────────
    // MAIN ENTRY POINT — called for every order
    // ─────────────────────────────────────────
    public List<Trade> ProcessOrder(Order order)
    {
        lock (_lock)
        {
            // Get or create the order book for this trading pair
            if (!_orderBooks.TryGetValue(order.TradingPair, out var book))
            {
                book = new OrderBook(order.TradingPair);
                _orderBooks[order.TradingPair] = book;
            }

            var newTrades = order.Side == OrderSide.Buy
                ? MatchBuyOrder(order, book)
                : MatchSellOrder(order, book);

            // If order still has remaining quantity — it rests in the book
            if (order.RemainingQuantity > 0)
                AddToBook(order, book);

            _trades.AddRange(newTrades);
            return newTrades;
        }
    }

    // ─────────────────────────────────────────
    // MATCH A BUY ORDER AGAINST THE SELL SIDE
    // ─────────────────────────────────────────
    private List<Trade> MatchBuyOrder(Order buyOrder, OrderBook book)
    {
        var trades = new List<Trade>();

        // Keep matching as long as:
        // 1. The buyer still needs more
        // 2. There are sellers available
        // 3. The cheapest seller's price is within the buyer's limit
        while (buyOrder.RemainingQuantity > 0
               && book.Asks.Count > 0
               && book.BestAsk <= buyOrder.Price)
        {
            // Peek at the cheapest seller price level
            var bestAskPrice = book.Asks.Keys.First();
            var sellQueue = book.Asks[bestAskPrice];
            var sellOrder = sellQueue.Peek(); // Look at first seller without removing

            // How much can we actually trade right now?
            var fillQty = Math.Min(buyOrder.RemainingQuantity, sellOrder.RemainingQuantity);

            // Execute — update both orders
            buyOrder.FilledQuantity += fillQty;
            sellOrder.FilledQuantity += fillQty;

            // Trade executes at the SELLER's price
            // (buyer said up to $60k, seller said $59.5k — buyer gets the better deal)
            var trade = new Trade
            {
                TradingPair = buyOrder.TradingPair,
                BuyOrderId = buyOrder.Id,
                SellOrderId = sellOrder.Id,
                BuyerUserId = buyOrder.UserId,
                SellerUserId = sellOrder.UserId,
                Price = bestAskPrice,       // Actual execution price
                Quantity = fillQty
            };

            trades.Add(trade);

            // Update statuses
            buyOrder.Status = buyOrder.RemainingQuantity == 0
                ? OrderStatus.Filled
                : OrderStatus.PartiallyFilled;

            sellOrder.Status = sellOrder.RemainingQuantity == 0
                ? OrderStatus.Filled
                : OrderStatus.PartiallyFilled;

            // If the sell order is fully filled — remove it from the book
            if (sellOrder.RemainingQuantity == 0)
            {
                sellQueue.Dequeue(); // Remove from queue

                // If no more orders at this price level — remove the price level entirely
                if (sellQueue.Count == 0)
                    book.Asks.Remove(bestAskPrice);
            }
            // If sell order is partially filled — it stays at front of queue (Peek left it there)
        }

        return trades;
    }

    // ─────────────────────────────────────────
    // MATCH A SELL ORDER AGAINST THE BUY SIDE
    // ─────────────────────────────────────────
    private List<Trade> MatchSellOrder(Order sellOrder, OrderBook book)
    {
        var trades = new List<Trade>();

        // Mirror logic — seller matches against highest bidder
        // Condition: highest buyer's price >= seller's asking price
        while (sellOrder.RemainingQuantity > 0
               && book.Bids.Count > 0
               && book.BestBid >= sellOrder.Price)
        {
            var bestBidPrice = book.Bids.Keys.First();
            var buyQueue = book.Bids[bestBidPrice];
            var buyOrder = buyQueue.Peek();

            var fillQty = Math.Min(sellOrder.RemainingQuantity, buyOrder.RemainingQuantity);

            sellOrder.FilledQuantity += fillQty;
            buyOrder.FilledQuantity += fillQty;

            // Trade executes at the BUYER's price
            // (seller said $59.5k minimum, buyer already bid $60k — seller gets the better deal)
            var trade = new Trade
            {
                TradingPair = sellOrder.TradingPair,
                BuyOrderId = buyOrder.Id,
                SellOrderId = sellOrder.Id,
                BuyerUserId = buyOrder.UserId,
                SellerUserId = sellOrder.UserId,
                Price = bestBidPrice,
                Quantity = fillQty
            };

            trades.Add(trade);

            sellOrder.Status = sellOrder.RemainingQuantity == 0
                ? OrderStatus.Filled
                : OrderStatus.PartiallyFilled;

            buyOrder.Status = buyOrder.RemainingQuantity == 0
                ? OrderStatus.Filled
                : OrderStatus.PartiallyFilled;

            if (buyOrder.RemainingQuantity == 0)
            {
                buyQueue.Dequeue();
                if (buyQueue.Count == 0)
                    book.Bids.Remove(bestBidPrice);
            }
        }

        return trades;
    }

    // ─────────────────────────────────────────
    // ADD UNMATCHED ORDER TO THE BOOK (resting)
    // ─────────────────────────────────────────
    private void AddToBook(Order order, OrderBook book)
    {
        var side = order.Side == OrderSide.Buy ? book.Bids : book.Asks;

        // If no orders exist at this price level yet — create the queue
        if (!side.ContainsKey(order.Price))
            side[order.Price] = new Queue<Order>();

        // Add to back of queue — time priority maintained
        side[order.Price].Enqueue(order);

        if (order.Status == OrderStatus.Pending)
            order.Status = OrderStatus.Pending; // stays pending until any fill
    }

    // ─────────────────────────────────────────
    // INSPECT THE BOOK (for debugging/API)
    // ─────────────────────────────────────────
    public OrderBook? GetOrderBook(string tradingPair)
    {
        _orderBooks.TryGetValue(tradingPair, out var book);
        return book;
    }

    public (int totalOrders, int totalTrades) GetStats(string tradingPair)
    {
        if (!_orderBooks.TryGetValue(tradingPair, out var book))
            return (0, 0);

        var totalOrders = book.Bids.Values.Sum(q => q.Count)
                        + book.Asks.Values.Sum(q => q.Count);

        var totalTrades = _trades.Count(t => t.TradingPair == tradingPair);

        return (totalOrders, totalTrades);
    }
}