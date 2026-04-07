using Exchange.Core.Persistence.Repositories;
using Exchange.Core.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace Exchange.Core.Accounts;

public class AccountService
{
    private readonly AccountRepository       _repo;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        AccountRepository repo,
        ILogger<AccountService> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    public async Task<AccountEntity> DepositAsync(
        string userId, string currency, decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Deposit amount must be positive.");

        var account = await _repo.DepositAsync(userId, currency.ToUpper(), amount);

        _logger.LogInformation(
            "DEPOSIT | User: {User} | {Amount} {Currency} | New balance: {Balance}",
            userId, amount, currency, account.TotalBalance);

        return account;
    }

    public async Task<string?> ValidateAndLockAsync(
        Guid    orderId,
        string  userId,
        string  tradingPair,
        string  side,
        decimal price,
        decimal quantity,
        string  orderPayload,
        string  kafkaTopic)
    {
        var parts    = tradingPair.Split('/');
        var baseCcy  = parts[0];
        var quoteCcy = parts[1];

        var outboxMessage = new OutboxMessageEntity
        {
            Id          = Guid.NewGuid(),
            Topic       = kafkaTopic,
            MessageKey  = tradingPair,
            Payload     = orderPayload,
            MessageType = "OrderPlaced",
            CreatedAt   = DateTime.UtcNow
        };

        if (side == "Buy")
        {
            var required = price * quantity;
            var locked   = await _repo.TryLockFundsAsync(
                orderId, userId, quoteCcy, required, outboxMessage);

            if (!locked)
            {
                var account   = await _repo.GetAccountAsync(userId, quoteCcy);
                var available = account?.TotalBalance - account?.LockedBalance ?? 0;

                _logger.LogWarning(
                    "INSUFFICIENT FUNDS | User: {User} | " +
                    "Required: {Required} {Currency} | Available: {Available}",
                    userId, required, quoteCcy, available);

                return $"Insufficient {quoteCcy}. " +
                       $"Required: {required:F2}, Available: {available:F2}";
            }

            _logger.LogInformation(
                "FUNDS LOCKED + OUTBOX WRITTEN | User: {User} | " +
                "{Amount} {Currency} for order {OrderId}",
                userId, required, quoteCcy, orderId);
        }
        else
        {
            var locked = await _repo.TryLockFundsAsync(
                orderId, userId, baseCcy, quantity, outboxMessage);

            if (!locked)
            {
                var account   = await _repo.GetAccountAsync(userId, baseCcy);
                var available = account?.TotalBalance - account?.LockedBalance ?? 0;

                _logger.LogWarning(
                    "INSUFFICIENT FUNDS | User: {User} | " +
                    "Required: {Required} {Currency} | Available: {Available}",
                    userId, quantity, baseCcy, available);

                return $"Insufficient {baseCcy}. " +
                       $"Required: {quantity:F8}, Available: {available:F8}";
            }

            _logger.LogInformation(
                "FUNDS LOCKED + OUTBOX WRITTEN | User: {User} | " +
                "{Amount} {Currency} for order {OrderId}",
                userId, quantity, baseCcy, orderId);
        }

        return null;
    }

    public async Task<List<AccountEntity>> GetBalancesAsync(string userId)
        => await _repo.GetUserAccountsAsync(userId);
}