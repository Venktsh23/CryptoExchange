namespace Exchange.Core.Persistence.Entities;

public class OutboxMessageEntity
{
    public Guid      Id          { get; set; } = Guid.NewGuid();
    public string    Topic       { get; set; } = string.Empty;
    public string    MessageKey  { get; set; } = string.Empty;
    public string    Payload     { get; set; } = string.Empty;
    public string    MessageType { get; set; } = string.Empty;
    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public int       RetryCount  { get; set; } = 0;
    public string?   LastError   { get; set; }
}