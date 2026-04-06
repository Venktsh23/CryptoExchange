using Microsoft.EntityFrameworkCore;
using Exchange.Core.Accounts.Models;
using Exchange.Core.Persistence.Entities;
using Polly;
using Microsoft.Extensions.Logging;

namespace Exchange.Core.Persistence.Repositories;

public class AccountRepository
{
  private readonly ExchangeDbContext          _context;
// private readonly ResiliencePipeline         _dbPipeline;
private readonly ILogger<AccountRepository> _logger;

  public AccountRepository(
    ExchangeDbContext context,
    ILogger<AccountRepository> logger)
{
    _context    = context;
    _logger     = logger;
    // _dbPipeline = PollyExtensions
    //     .CreateDatabasePipeline(logger, "AccountDB");
}
    // Get account — returns null if doesn't exist
    public async Task<AccountEntity?> GetAccountAsync(
        string userId, string currency)
    {
        return await _context.Accounts
            .FirstOrDefaultAsync(a =>
                a.UserId   == userId &&
                a.Currency == currency);
    }

    // Get all accounts for a user
    public async Task<List<AccountEntity>> GetUserAccountsAsync(string userId)
    {
        return await _context.Accounts
            .Where(a => a.UserId == userId)
            .ToListAsync();
    }

    // Deposit — creates account if first deposit, adds to existing otherwise
    public async Task<AccountEntity> DepositAsync(
        string userId, string currency, decimal amount)
    {
        var account = await GetAccountAsync(userId, currency);

        if (account == null)
        {
            // First deposit — create the account
            account = new AccountEntity
            {
                Id            = Guid.NewGuid(),
                UserId        = userId,
                Currency      = currency,
                TotalBalance  = amount,
                LockedBalance = 0,
                CreatedAt     = DateTime.UtcNow,
                UpdatedAt     = DateTime.UtcNow
            };
            _context.Accounts.Add(account);
        }
        else
        {
            account.TotalBalance += amount;
            account.UpdatedAt    =  DateTime.UtcNow;
        }

        // Record the transaction
        _context.AccountTransactions.Add(new AccountTransactionEntity
        {
            Id          = Guid.NewGuid(),
            UserId      = userId,
            Currency    = currency,
            Amount      = amount,
            Type        = TransactionType.Deposit.ToString(),
            Description = $"Deposit {amount} {currency}",
            CreatedAt   = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        return account;
    }

    // Lock funds for a pending order
    // Returns false if insufficient available balance
   public async Task<bool> TryLockFundsAsync(
    Guid    orderId,
    string  userId,
    string  currency,
    decimal amount,
    OutboxMessageEntity? outboxMessage = null)  // ← add this parameter
{
    for (int attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            var account = await GetAccountAsync(userId, currency);
            if (account == null) return false;

            var available = account.TotalBalance - account.LockedBalance;
            if (available < amount) return false;

            account.LockedBalance += amount;
            account.UpdatedAt     =  DateTime.UtcNow;

            _context.FundLocks.Add(new FundLockEntity
            {
                Id        = Guid.NewGuid(),
                OrderId   = orderId,
                UserId    = userId,
                Currency  = currency,
                Amount    = amount,
                Status    = "Active",
                CreatedAt = DateTime.UtcNow
            });

            _context.AccountTransactions.Add(new AccountTransactionEntity
            {
                Id          = Guid.NewGuid(),
                UserId      = userId,
                Currency    = currency,
                Amount      = -amount,
                Type        = TransactionType.LockFunds.ToString(),
                ReferenceId = orderId,
                Description = $"Lock {amount} {currency} for order {orderId}",
                CreatedAt   = DateTime.UtcNow
            });

            // THE KEY LINE — same SaveChangesAsync, same transaction
            if (outboxMessage != null)
                _context.OutboxMessages.Add(outboxMessage);

            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            if (attempt == 2) throw;
            await Task.Delay(50 * (attempt + 1));
        }
    }

    return false;
}

    // Release locked funds — order cancelled
    public async Task ReleaseFundsAsync(Guid orderId)
    {
        var lock_ = await _context.FundLocks
            .FirstOrDefaultAsync(f =>
                f.OrderId == orderId &&
                f.Status  == "Active");

        if (lock_ == null) return;

        var account = await GetAccountAsync(lock_.UserId, lock_.Currency);
        if (account == null) return;

        // Release the lock
        account.LockedBalance -= lock_.Amount;
        account.UpdatedAt     =  DateTime.UtcNow;

        lock_.Status     = "Released";
        lock_.ReleasedAt = DateTime.UtcNow;

        _context.AccountTransactions.Add(new AccountTransactionEntity
        {
            Id          = Guid.NewGuid(),
            UserId      = lock_.UserId,
            Currency    = lock_.Currency,
            Amount      = lock_.Amount, // Positive = funds returned
            Type        = TransactionType.ReleaseFunds.ToString(),
            ReferenceId = orderId,
            Description = $"Release {lock_.Amount} {lock_.Currency} " +
                          $"for cancelled order {orderId}",
            CreatedAt   = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
    }

    // Transfer funds when trade executes
    // Buyer: USD deducted, crypto added
    // Seller: crypto deducted, USD added
    public async Task TransferTradeAsync(
    string  buyerUserId,
    string  sellerUserId,
    string  tradingPair,
    decimal quantity,
    decimal price,
    Guid    tradeId,
    Guid    buyOrderId,    // ← find exact buyer lock
    Guid    sellOrderId)   // ← find exact seller lock
{
    var parts     = tradingPair.Split('/');
    var baseCcy   = parts[0]; // BTC
    var quoteCcy  = parts[1]; // USD
    var usdAmount = quantity * price;

    // Load all accounts
    var buyerUsd  = await GetOrCreateAccountAsync(buyerUserId,  quoteCcy);
    var buyerBtc  = await GetOrCreateAccountAsync(buyerUserId,  baseCcy);
    var sellerBtc = await GetOrCreateAccountAsync(sellerUserId, baseCcy);
    var sellerUsd = await GetOrCreateAccountAsync(sellerUserId, quoteCcy);

    // Find exact locks by OrderId — not by user+currency
    var buyerLock = await _context.FundLocks
        .FirstOrDefaultAsync(f =>
            f.OrderId == buyOrderId &&
            f.Status  == "Active");

    var sellerLock = await _context.FundLocks
        .FirstOrDefaultAsync(f =>
            f.OrderId == sellOrderId &&
            f.Status  == "Active");

    // Update buyer balances
    buyerUsd.TotalBalance  -= usdAmount;
    buyerUsd.LockedBalance -= usdAmount;
    buyerUsd.UpdatedAt     =  DateTime.UtcNow;

    buyerBtc.TotalBalance += quantity;
    buyerBtc.UpdatedAt    =  DateTime.UtcNow;

    // Update seller balances
    sellerBtc.TotalBalance  -= quantity;
    sellerBtc.LockedBalance -= quantity;
    sellerBtc.UpdatedAt     =  DateTime.UtcNow;

    sellerUsd.TotalBalance += usdAmount;
    sellerUsd.UpdatedAt    =  DateTime.UtcNow;

    // Consume the locks
    if (buyerLock != null)
    {
        buyerLock.Status     = "Consumed";
        buyerLock.ReleasedAt = DateTime.UtcNow;
    }
    else
    {
        _logger.LogWarning(
            "Buyer lock not found for order {OrderId}", buyOrderId);
    }

    if (sellerLock != null)
    {
        sellerLock.Status     = "Consumed";
        sellerLock.ReleasedAt = DateTime.UtcNow;
    }
    else
    {
        _logger.LogWarning(
            "Seller lock not found for order {OrderId}", sellOrderId);
    }

    // Record transactions
    _context.AccountTransactions.AddRange(new[]
    {
        new AccountTransactionEntity
        {
            Id          = Guid.NewGuid(),
            UserId      = buyerUserId,
            Currency    = quoteCcy,
            Amount      = -usdAmount,
            Type        = TransactionType.TradeBuy.ToString(),
            ReferenceId = tradeId,
            Description = $"Buy {quantity} {baseCcy} @ {price}",
            CreatedAt   = DateTime.UtcNow
        },
        new AccountTransactionEntity
        {
            Id          = Guid.NewGuid(),
            UserId      = buyerUserId,
            Currency    = baseCcy,
            Amount      = quantity,
            Type        = TransactionType.TradeBuy.ToString(),
            ReferenceId = tradeId,
            Description = $"Received {quantity} {baseCcy}",
            CreatedAt   = DateTime.UtcNow
        },
        new AccountTransactionEntity
        {
            Id          = Guid.NewGuid(),
            UserId      = sellerUserId,
            Currency    = baseCcy,
            Amount      = -quantity,
            Type        = TransactionType.TradeSell.ToString(),
            ReferenceId = tradeId,
            Description = $"Sold {quantity} {baseCcy} @ {price}",
            CreatedAt   = DateTime.UtcNow
        },
        new AccountTransactionEntity
        {
            Id          = Guid.NewGuid(),
            UserId      = sellerUserId,
            Currency    = quoteCcy,
            Amount      = usdAmount,
            Type        = TransactionType.TradeSell.ToString(),
            ReferenceId = tradeId,
            Description = $"Received {usdAmount} {quoteCcy}",
            CreatedAt   = DateTime.UtcNow
        }
    });

    await _context.SaveChangesAsync();
}

// Helper — gets existing account or creates a zero-balance one
private async Task<AccountEntity> GetOrCreateAccountAsync(
    string userId, string currency)
{
    var account = await _context.Accounts
        .FirstOrDefaultAsync(a =>
            a.UserId   == userId &&
            a.Currency == currency);

    if (account != null) return account;

    // Create new zero-balance account
    account = new AccountEntity
    {
        Id            = Guid.NewGuid(),
        UserId        = userId,
        Currency      = currency,
        TotalBalance  = 0,
        LockedBalance = 0,
        CreatedAt     = DateTime.UtcNow,
        UpdatedAt     = DateTime.UtcNow
    };

    _context.Accounts.Add(account);
    return account;
}



}