namespace Exchange.Core.Persistence.Entities;

public class OrderBookSnapshotEntity
{
    public int Id { get; set; }
    public string TradingPair { get; set; } = string.Empty;

    // Entire order book serialized as JSON
    // Simpler than separate tables — snapshots are read/written whole
    public string SnapshotJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int RestingOrderCount { get; set; }
}