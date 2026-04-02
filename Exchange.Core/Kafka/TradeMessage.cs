namespace Exchange.Core.Kafka;

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
}