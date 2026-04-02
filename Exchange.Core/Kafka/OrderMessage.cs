namespace Exchange.Core.Kafka;

// Kafka message contract for an incoming order
// Once in production, fields cannot be removed or renamed
// New optional fields can be added safely
public class OrderMessage
{
    public Guid     Id          { get; set; }
    public string   UserId      { get; set; } = string.Empty;
    public string   TradingPair { get; set; } = string.Empty;
    public string   Side        { get; set; } = string.Empty; // "Buy" or "Sell"
    public decimal  Price       { get; set; }
    public decimal  Quantity    { get; set; }
    public DateTime CreatedAt   { get; set; }
    public int      Version     { get; set; } = 1;
}