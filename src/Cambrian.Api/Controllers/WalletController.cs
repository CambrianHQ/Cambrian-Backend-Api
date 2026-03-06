using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("wallet")]
[Authorize]
public class WalletController : BaseController
{
    [HttpGet]
    public IActionResult Get()
    {
        return OkResponse(new { balance = 0m, currency = "usd" });
    }

    [HttpPost("withdraw")]
    public IActionResult Withdraw()
    {
        return OkResponse(new { status = "pending" });
    }

    [HttpGet("history")]
    public IActionResult History([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        return OkResponse(Array.Empty<object>());
    }
}
