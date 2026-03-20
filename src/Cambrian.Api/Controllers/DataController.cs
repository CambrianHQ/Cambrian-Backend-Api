using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("data")]
[Authorize]
public class DataController : BaseController
{
    private readonly UserManager<Cambrian.Domain.Entities.ApplicationUser> _users;
    private readonly ISubscriptionRepository _subscriptions;

    public DataController(UserManager<Cambrian.Domain.Entities.ApplicationUser> users, ISubscriptionRepository subscriptions)
    {
        _users = users;
        _subscriptions = subscriptions;
    }

    [HttpGet("account")]
    public async Task<IActionResult> GetAccount()
    {
        var userId = GetRequiredUserId()!;
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return NotFoundResponse("User not found.");

        var sub = await _subscriptions.GetActiveAsync(userId);
        return OkResponse(new
        {
            id = user.Id,
            email = user.Email,
            plan = sub?.Plan ?? user.Tier ?? "free",
            region = "US",
            status = user.Status ?? "active"
        });
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
