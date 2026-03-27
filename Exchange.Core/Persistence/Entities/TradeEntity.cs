namespace Exchange.Core.Persistence.Entities;

public class TradeEntity
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

    // When we saved it to DB — different from ExecutedAt
    // ExecutedAt = when the engine matched it (could be milliseconds earlier)
    // PersistedAt = when this row was written to PostgreSQL
    public DateTime PersistedAt { get; set; } = DateTime.UtcNow;
}