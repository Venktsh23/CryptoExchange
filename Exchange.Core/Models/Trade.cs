namespace Exchange.Core.Models;

public class Trade
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string TradingPair { get; init; } = string.Empty;
    public Guid BuyOrderId { get; init; }
    public Guid SellOrderId { get; init; }
    public string BuyerUserId { get; init; } = string.Empty;
    public string SellerUserId { get; init; } = string.Empty;
    public decimal Price { get; init; }      // Price it actually executed at
    public decimal Quantity { get; init; }   // How much was traded
    public decimal TotalValue => Price * Quantity;  // e.g. 60000 * 0.8 = 48000
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;
}