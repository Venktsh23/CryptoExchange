using Microsoft.AspNetCore.Mvc;
using Exchange.Core.Accounts;

namespace Exchange.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountsController : ControllerBase
{
    private readonly AccountService _accountService;

    public AccountsController(AccountService accountService)
    {
        _accountService = accountService;
    }

    // Deposit funds — for testing purposes
    // In production this would connect to a payment processor
    [HttpPost("deposit")]
    public async Task<IActionResult> Deposit(
        [FromBody] DepositRequest request)
    {
        if (request.Amount <= 0)
            return BadRequest("Amount must be positive.");

        if (string.IsNullOrWhiteSpace(request.Currency))
            return BadRequest("Currency required. e.g. USD, BTC");

        var account = await _accountService.DepositAsync(
            request.UserId,
            request.Currency,
            request.Amount);

        return Ok(new
        {
            userId           = account.UserId,
            currency         = account.Currency,
            totalBalance     = account.TotalBalance,
            lockedBalance    = account.LockedBalance,
            availableBalance = account.TotalBalance - account.LockedBalance
        });
    }

    // View all balances for a user
    [HttpGet("{userId}")]
    public async Task<IActionResult> GetBalances(string userId)
    {
        var accounts = await _accountService.GetBalancesAsync(userId);

        if (!accounts.Any())
            return NotFound($"No accounts found for user {userId}");

        return Ok(accounts.Select(a => new
        {
            currency         = a.Currency,
            totalBalance     = a.TotalBalance,
            lockedBalance    = a.LockedBalance,
            availableBalance = a.TotalBalance - a.LockedBalance,
            updatedAt        = a.UpdatedAt
        }));
    }
}

public record DepositRequest(
    string  UserId,
    string  Currency,
    decimal Amount
);