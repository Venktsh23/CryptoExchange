namespace Exchange.Core.Models;

public enum OrderSide { Buy, Sell }
public enum OrderStatus { Pending, PartiallyFilled, Filled, Cancelled }

public class Order
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string UserId { get; init; } = string.Empty;
    public string TradingPair { get; init; } = string.Empty; // e.g. "BTC/USD"
    public OrderSide Side { get; init; }
    public decimal Price { get; init; }          // Price per 1 unit e.g. 60000
    public decimal Quantity { get; init; }       // How many units e.g. 2.5
    public decimal FilledQuantity { get; set; }  // How much has been matched so far
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public decimal RemainingQuantity => Quantity - FilledQuantity;
}