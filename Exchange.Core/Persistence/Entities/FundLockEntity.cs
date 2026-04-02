namespace Exchange.Core.Persistence.Entities;

public class FundLockEntity
{
    public Guid     Id         { get; set; }
    public Guid     OrderId    { get; set; }
    public string   UserId     { get; set; } = string.Empty;
    public string   Currency   { get; set; } = string.Empty;
    public decimal  Amount     { get; set; }
    public string   Status     { get; set; } = "Active";
    public DateTime CreatedAt  { get; set; }
    public DateTime? ReleasedAt { get; set; }
}