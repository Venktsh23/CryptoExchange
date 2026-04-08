using Microsoft.EntityFrameworkCore;
using Exchange.Core.Accounts.Models;
using Exchange.Core.Persistence.Entities;
using Exchange.Core.Resilience;
using Polly;
using Microsoft.Extensions.Logging;

namespace Exchange.Core.Persistence.Repositories;

public class AccountRepository
{
    private readonly ExchangeDbContext          _context;
    private readonly ResiliencePipeline         _dbPipeline;
    private readonly ILogger<AccountRepository> _logger;

    public AccountRepository(
        ExchangeDbContext context,
        ILogger<AccountRepository> logger)
    {
        _context    = context;
        _logger     = logger;
        _dbPipeline = ResiliencePipelineFactory
            .CreateDatabasePipeline(logger, "AccountDB");
    }

    public async Task<AccountEntity?> GetAccountAsync(
        string userId, string currency)
    {
        return await _dbPipeline.ExecuteAsync(async ct =>
            await _context.Accounts
                .FirstOrDefaultAsync(a =>
                    a.UserId   == userId &&
                    a.Currency == currency, ct));
    }

    public async Task<List<AccountEntity>> GetUserAccountsAsync(string userId)
    {
        return await _dbPipeline.ExecuteAsync(async ct =>
            await _context.Accounts
                .Where(a => a.UserId == userId)
                .ToListAsync(ct));
    }

    public async Task<AccountEntity> DepositAsync(
        string userId, string currency, decimal amount)
    {
        var account = await GetAccountAsync(userId, currency);

        if (account == null)
        {
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

        await _dbPipeline.ExecuteAsync(async ct =>
            await _context.SaveChangesAsync(ct));

        return account;
    }

    public async Task<bool> TryLockFundsAsync(
        Guid    orderId,
        string  userId,
        string  currency,
        decimal amount,
        OutboxMessageEntity? outboxMessage = null)
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

                if (outboxMessage != null)
                    _context.OutboxMessages.Add(outboxMessage);

                await _dbPipeline.ExecuteAsync(async ct =>
                    await _context.SaveChangesAsync(ct));

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

    public async Task ReleaseFundsAsync(Guid orderId)
    {
        var lock_ = await _dbPipeline.ExecuteAsync(async ct =>
            await _context.FundLocks
                .FirstOrDefaultAsync(f =>
                    f.OrderId == orderId &&
                    f.Status  == "Active", ct));

        if (lock_ == null) return;

        var account = await GetAccountAsync(lock_.UserId, lock_.Currency);
        if (account == null) return;

        account.LockedBalance -= lock_.Amount;
        account.UpdatedAt     =  DateTime.UtcNow;

        lock_.Status     = "Released";
        lock_.ReleasedAt = DateTime.UtcNow;

        _context.AccountTransactions.Add(new AccountTransactionEntity
        {
            Id          = Guid.NewGuid(),
            UserId      = lock_.UserId,
            Currency    = lock_.Currency,
            Amount      = lock_.Amount,
            Type        = TransactionType.ReleaseFunds.ToString(),
            ReferenceId = orderId,
            Description = $"Release {lock_.Amount} {lock_.Currency} for cancelled order {orderId}",
            CreatedAt   = DateTime.UtcNow
        });

        await _dbPipeline.ExecuteAsync(async ct =>
            await _context.SaveChangesAsync(ct));
    }

    public async Task TransferTradeAsync(
        string  buyerUserId,
        string  sellerUserId,
        string  tradingPair,
        decimal quantity,
        decimal price,
        Guid    tradeId,
        Guid    buyOrderId,
        Guid    sellOrderId)
    {
        var parts     = tradingPair.Split('/');
        var baseCcy   = parts[0];
        var quoteCcy  = parts[1];
        var usdAmount = quantity * price;

        var buyerUsd  = await GetOrCreateAccountAsync(buyerUserId,  quoteCcy);
        var buyerBtc  = await GetOrCreateAccountAsync(buyerUserId,  baseCcy);
        var sellerBtc = await GetOrCreateAccountAsync(sellerUserId, baseCcy);
        var sellerUsd = await GetOrCreateAccountAsync(sellerUserId, quoteCcy);

        var buyerLock = await _dbPipeline.ExecuteAsync(async ct =>
            await _context.FundLocks
                .FirstOrDefaultAsync(f =>
                    f.OrderId == buyOrderId &&
                    f.Status  == "Active", ct));

        var sellerLock = await _dbPipeline.ExecuteAsync(async ct =>
            await _context.FundLocks
                .FirstOrDefaultAsync(f =>
                    f.OrderId == sellOrderId &&
                    f.Status  == "Active", ct));

        buyerUsd.TotalBalance  -= usdAmount;
        buyerUsd.LockedBalance -= usdAmount;
        buyerUsd.UpdatedAt     =  DateTime.UtcNow;

        buyerBtc.TotalBalance += quantity;
        buyerBtc.UpdatedAt    =  DateTime.UtcNow;

        sellerBtc.TotalBalance  -= quantity;
        sellerBtc.LockedBalance -= quantity;
        sellerBtc.UpdatedAt     =  DateTime.UtcNow;

        sellerUsd.TotalBalance += usdAmount;
        sellerUsd.UpdatedAt    =  DateTime.UtcNow;

        if (buyerLock != null)
        {
            buyerLock.Status     = "Consumed";
            buyerLock.ReleasedAt = DateTime.UtcNow;
        }
        else
            _logger.LogWarning("Buyer lock not found for order {OrderId}", buyOrderId);

        if (sellerLock != null)
        {
            sellerLock.Status     = "Consumed";
            sellerLock.ReleasedAt = DateTime.UtcNow;
        }
        else
            _logger.LogWarning("Seller lock not found for order {OrderId}", sellOrderId);

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

        await _dbPipeline.ExecuteAsync(async ct =>
            await _context.SaveChangesAsync(ct));
    }

    private async Task<AccountEntity> GetOrCreateAccountAsync(
        string userId, string currency)
    {
        var account = await _context.Accounts
            .FirstOrDefaultAsync(a =>
                a.UserId   == userId &&
                a.Currency == currency);

        if (account != null) return account;

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