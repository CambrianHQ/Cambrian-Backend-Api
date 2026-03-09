using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("data")]
[Authorize]
public class DataController : BaseController
{
    private readonly IAccountService _account;

    public DataController(IAccountService account)
    {
        _account = account;
    }

    [HttpGet("account")]
    public async Task<IActionResult> GetAccount()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        try
        {
            var account = await _account.GetAccountAsync(userId);
            return OkResponse(account);
        }
        catch (KeyNotFoundException)
        {
            return NotFoundResponse("User not found.");
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("songs")]
    public IActionResult GetSongs()
    {
        return OkResponse(Array.Empty<object>());
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("songs")]
    public IActionResult PostSongs()
    {
        return MessageResponse("Songs data received.");
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("system")]
    public IActionResult GetSystem()
    {
        return OkResponse(new { });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("system")]
    public IActionResult PostSystem()
    {
        return MessageResponse("System data received.");
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("secrets")]
    public IActionResult GetSecrets()
    {
        return OkResponse(new { });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("secrets")]
    public IActionResult PostSecrets()
    {
        return MessageResponse("Secrets data received.");
    }
}
