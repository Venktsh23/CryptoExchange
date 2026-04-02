namespace Exchange.Core.Persistence.Entities;

public class AccountEntity
{
    public Guid     Id            { get; set; }
    public string   UserId        { get; set; } = string.Empty;
    public string   Currency      { get; set; } = string.Empty;
    public decimal  TotalBalance  { get; set; }
    public decimal  LockedBalance { get; set; }
    public DateTime CreatedAt     { get; set; }
    public DateTime UpdatedAt     { get; set; }

    // Optimistic concurrency — prevents two simultaneous
    // balance updates from corrupting data
    // PostgreSQL checks this version matches before updating
    public uint RowVersion { get; set; }
}