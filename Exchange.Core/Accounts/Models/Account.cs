namespace Exchange.Core.Accounts.Models;

public class Account
{
    public Guid    Id               { get; set; } = Guid.NewGuid();
    public string  UserId           { get; set; } = string.Empty;
    public string  Currency         { get; set; } = string.Empty; // "USD", "BTC", "ETH"
    public decimal TotalBalance     { get; set; }
    public decimal LockedBalance    { get; set; }

    // Never stored — always computed
    // This is the only balance that matters for new orders
    public decimal AvailableBalance => TotalBalance - LockedBalance;

    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;
}