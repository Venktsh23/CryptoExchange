namespace Exchange.Core.Persistence.Entities;

public class AccountTransactionEntity
{
    public Guid     Id          { get; set; }
    public string   UserId      { get; set; } = string.Empty;
    public string   Currency    { get; set; } = string.Empty;
    public decimal  Amount      { get; set; }
    public string   Type        { get; set; } = string.Empty;
    public Guid?    ReferenceId { get; set; }
    public string   Description { get; set; } = string.Empty;
    public DateTime CreatedAt   { get; set; }
}