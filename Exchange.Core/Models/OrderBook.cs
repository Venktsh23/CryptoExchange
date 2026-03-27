namespace Exchange.Core.Models;

public class OrderBook
{
    public string TradingPair { get; init; }

    // Sell side — sorted lowest price first (cheapest seller at top)
    // Key = Price, Value = Queue of orders at that price (time priority)
    public SortedDictionary<decimal, Queue<Order>> Asks { get; } =
        new SortedDictionary<decimal, Queue<Order>>();

    // Buy side — sorted highest price first (most willing buyer at top)
    // We reverse the comparer so highest price comes first
    public SortedDictionary<decimal, Queue<Order>> Bids { get; } =
        new SortedDictionary<decimal, Queue<Order>>(
            Comparer<decimal>.Create((a, b) => b.CompareTo(a))
        );

    public OrderBook(string tradingPair)
    {
        TradingPair = tradingPair;
    }

    // Best price a buyer is willing to pay right now
    public decimal? BestBid => Bids.Count > 0 ? Bids.Keys.First() : null;

    // Best price a seller is willing to accept right now
    public decimal? BestAsk => Asks.Count > 0 ? Asks.Keys.First() : null;

    // The gap between best buy and best sell
    public decimal? Spread => BestAsk - BestBid;
}


// ```

// **The most important part — why `Queue<Order>` inside the dictionary:**

// The dictionary key is the **price level** (e.g. $60,000). At that price level, multiple orders can be waiting. The Queue maintains **time priority** — first order in is first order matched. This is exactly the deli ticket system from our earlier explanation.
// ```
// Bids dictionary:
//   $60,100 → [Order A (arrived first), Order B (arrived second)]
//   $60,000 → [Order C]
//   $59,900 → [Order D, Order E]

// Asks dictionary:
//   $60,200 → [Order F]
//   $60,300 → [Order G, Order H]