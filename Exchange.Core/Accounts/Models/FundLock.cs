namespace Exchange.Core.Accounts.Models;

public enum FundLockStatus { Active, Released, Consumed }

public class FundLock
{
    public Guid           Id         { get; set; } = Guid.NewGuid();
    public Guid           OrderId    { get; set; }
    public string         UserId     { get; set; } = string.Empty;
    public string         Currency   { get; set; } = string.Empty;
    public decimal        Amount     { get; set; }
    public FundLockStatus Status     { get; set; } = FundLockStatus.Active;
    public DateTime       CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime?      ReleasedAt { get; set; }
}