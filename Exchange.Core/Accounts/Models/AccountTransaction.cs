namespace Exchange.Core.Accounts.Models;

public enum TransactionType
{
    Deposit,       // Funds added to account
    Withdrawal,    // Funds removed from account
    TradeBuy,      // Bought crypto — USD deducted, crypto added
    TradeSell,     // Sold crypto — crypto deducted, USD added
    LockFunds,     // Funds locked for pending order
    ReleaseFunds   // Locked funds released (order cancelled)
}

public class AccountTransaction
{
    public Guid            Id          { get; set; } = Guid.NewGuid();
    public string          UserId      { get; set; } = string.Empty;
    public string          Currency    { get; set; } = string.Empty;
    public decimal         Amount      { get; set; } // Positive = credit, Negative = debit
    public TransactionType Type        { get; set; }
    public Guid?           ReferenceId { get; set; } // OrderId or TradeId
    public string          Description { get; set; } = string.Empty;
    public DateTime        CreatedAt   { get; set; } = DateTime.UtcNow;
}