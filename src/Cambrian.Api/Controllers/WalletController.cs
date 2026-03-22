using Cambrian.Application.DTOs.Wallet;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("wallet")]
[Authorize]
public class WalletController : BaseController
{
    private readonly IWalletService _wallet;

    public WalletController(IWalletService wallet)
    {
        _wallet = wallet;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = GetRequiredUserId()!;
        var balance = await _wallet.GetBalanceAsync(userId);
        return OkResponse(balance);
    }

    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw([FromBody] WithdrawRequest request)
    {
        var userId = GetRequiredUserId()!;

        try
        {
            await _wallet.WithdrawAsync(request.Amount, userId);
            return OkResponse(new { status = "pending" });
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResponse(ex.Message);
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        var userId = GetRequiredUserId()!;
        var history = await _wallet.GetHistoryAsync(userId, pageSize);
        return OkResponse(history);
    }
}
