namespace Exchange.Core.Kafka;

// This is the Kafka message contract
// Serialized to JSON, written to the trades topic
// Must never have breaking changes — old messages must always be readable
public class TradeMessage
{
    public Guid Id { get; set; }
    public string TradingPair { get; set; } = string.Empty;
    public Guid BuyOrderId { get; set; }
    public Guid SellOrderId { get; set; }
    public string BuyerUserId { get; set; } = string.Empty;
    public string SellerUserId { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal TotalValue { get; set; }
    public DateTime ExecutedAt { get; set; }

    // Which version of this message format this is
    // If you ever add fields, bump this — consumers can handle both versions
    public int SchemaVersion { get; set; } = 1;
}