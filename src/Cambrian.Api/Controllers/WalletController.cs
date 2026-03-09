using System.Security.Claims;
using Cambrian.Application.DTOs.Wallet;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("wallet")]
[Authorize]
public class WalletController : BaseController
{
    private readonly IWalletRepository _wallet;

    public WalletController(IWalletRepository wallet)
    {
        _wallet = wallet;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var balanceCents = await _wallet.GetBalanceAsync(userId);

        return OkResponse(new WalletResponse
        {
            BalanceCents = balanceCents,
            Currency = "usd"
        });
    }

    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw(WithdrawRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var balanceCents = await _wallet.GetBalanceAsync(userId);
        var amountCents = (long)(request.Amount * 100);

        if (amountCents > balanceCents)
            return ErrorResponse("Insufficient balance.");

        var txn = new WalletTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AmountCents = -amountCents,
            Type = "withdrawal",
            Description = $"Withdrawal of ${request.Amount:F2}"
        };

        await _wallet.AddTransactionAsync(txn);
        return OkResponse(new { status = "pending" });
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await _wallet.GetHistoryAsync(userId, pageSize);
        return OkResponse(result);
    }
}
