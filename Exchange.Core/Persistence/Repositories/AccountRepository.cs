using Microsoft.EntityFrameworkCore;
using Exchange.Core.Accounts.Models;
using Exchange.Core.Persistence.Entities;

namespace Exchange.Core.Persistence.Repositories;

public class AccountRepository
{
    private readonly ExchangeDbContext _context;

    public AccountRepository(ExchangeDbContext context)
    {
        _context = context;
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
        Guid orderId, string userId, string currency, decimal amount)
    {
        // Retry loop for optimistic concurrency conflicts
        // If two requests hit simultaneously, one retries
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var account = await GetAccountAsync(userId, currency);

                if (account == null)
                    return false; // No account

                var available = account.TotalBalance - account.LockedBalance;

                if (available < amount)
                    return false; // Insufficient funds

                // Lock the funds
                account.LockedBalance += amount;
                account.UpdatedAt     =  DateTime.UtcNow;

                // Create the lock record
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

                // Record transaction
                _context.AccountTransactions.Add(new AccountTransactionEntity
                {
                    Id          = Guid.NewGuid(),
                    UserId      = userId,
                    Currency    = currency,
                    Amount      = -amount, // Negative = funds locked
                    Type        = TransactionType.LockFunds.ToString(),
                    ReferenceId = orderId,
                    Description = $"Lock {amount} {currency} for order {orderId}",
                    CreatedAt   = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request updated this account simultaneously
                // Reload and retry
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
        string buyerUserId,
        string sellerUserId,
        string tradingPair,       // e.g. "BTC/USD"
        decimal quantity,         // BTC amount
        decimal price,            // USD per BTC
        Guid tradeId)
    {
        // Parse trading pair — "BTC/USD" → base="BTC", quote="USD"
        var parts     = tradingPair.Split('/');
        var baseCcy   = parts[0]; // BTC
        var quoteCcy  = parts[1]; // USD
        var usdAmount = quantity * price;

        // Buyer: loses USD, gains BTC
        var buyerUsd = await GetAccountAsync(buyerUserId, quoteCcy);
        var buyerBtc = await GetAccountAsync(buyerUserId, baseCcy)
                    ?? new AccountEntity
                       {
                           Id        = Guid.NewGuid(),
                           UserId    = buyerUserId,
                           Currency  = baseCcy,
                           CreatedAt = DateTime.UtcNow
                       };

        // Seller: loses BTC, gains USD
        var sellerBtc = await GetAccountAsync(sellerUserId, baseCcy);
        var sellerUsd = await GetAccountAsync(sellerUserId, quoteCcy)
                     ?? new AccountEntity
                        {
                            Id        = Guid.NewGuid(),
                            UserId    = sellerUserId,
                            Currency  = quoteCcy,
                            CreatedAt = DateTime.UtcNow
                        };

        if (buyerUsd != null)
        {
            buyerUsd.TotalBalance  -= usdAmount;
            buyerUsd.LockedBalance -= usdAmount; // Consume the lock
            buyerUsd.UpdatedAt     =  DateTime.UtcNow;
        }

        buyerBtc.TotalBalance += quantity;
        buyerBtc.UpdatedAt    =  DateTime.UtcNow;

        if (sellerBtc != null)
        {
            sellerBtc.TotalBalance  -= quantity;
            sellerBtc.LockedBalance -= quantity; // Consume the lock
            sellerBtc.UpdatedAt     =  DateTime.UtcNow;
        }

        sellerUsd.TotalBalance += usdAmount;
        sellerUsd.UpdatedAt    =  DateTime.UtcNow;

        // Add new accounts if created
        if (buyerBtc.Id == Guid.Empty ||
            !await _context.Accounts.AnyAsync(a => a.Id == buyerBtc.Id))
            _context.Accounts.Add(buyerBtc);

        if (!await _context.Accounts.AnyAsync(a => a.Id == sellerUsd.Id))
            _context.Accounts.Add(sellerUsd);

        // Record all four transaction legs
        var transactions = new[]
        {
            new AccountTransactionEntity
            {
                Id          = Guid.NewGuid(),
                UserId      = buyerUserId,
                Currency    = quoteCcy,
                Amount      = -usdAmount,
                Type        = TransactionType.TradeBuy.ToString(),
                ReferenceId = tradeId,
                Description = $"Buy {quantity} {baseCcy} @ {price} {quoteCcy}",
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
                Description = $"Received {quantity} {baseCcy} from trade",
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
                Description = $"Sell {quantity} {baseCcy} @ {price} {quoteCcy}",
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
                Description = $"Received {usdAmount} {quoteCcy} from trade",
                CreatedAt   = DateTime.UtcNow
            }
        };

        _context.AccountTransactions.AddRange(transactions);
        await _context.SaveChangesAsync();
    }
}