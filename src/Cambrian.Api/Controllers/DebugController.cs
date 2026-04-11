using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Admin-only diagnostic endpoints for debugging user state, purchases, and library consistency.
/// </summary>
[Route("debug")]
[Authorize(Roles = "Admin")]
public class DebugController : BaseController
{
    private readonly IDebugService _debug;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DebugController> _logger;

    public DebugController(IDebugService debug, IWebHostEnvironment env, ILogger<DebugController> logger)
    {
        _debug = debug;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Returns full diagnostic state for a user: profile, tier, subscription, purchases, library items.
    /// </summary>
    [HttpGet("user/{userId}")]
    public async Task<IActionResult> UserState(string userId)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("EVENT: DebugUserState requested by admin:{AdminId} for user:{UserId}", adminId, userId);

        var result = await _debug.GetUserStateAsync(userId);
        if (result is null)
            return NotFoundResponse($"User {userId} not found.");

        return OkResponse(result);
    }

    /// <summary>
    /// Returns recent webhook events with optional filtering.
    /// </summary>
    [HttpGet("webhooks")]
    public async Task<IActionResult> RecentWebhooks([FromQuery] int limit = 25, [FromQuery] string? eventType = null)
    {
        var result = await _debug.GetRecentWebhooksAsync(limit, eventType);
        return OkResponse(result);
    }

    /// <summary>
    /// Library consistency check across all users: finds completed purchases with no matching library item.
    /// </summary>
    [HttpGet("consistency")]
    public async Task<IActionResult> ConsistencyCheck()
    {
        _logger.LogInformation("EVENT: ConsistencyCheck started by admin:{AdminId}",
            User.FindFirstValue(ClaimTypes.NameIdentifier));

        var result = await _debug.RunConsistencyCheckAsync();
        return OkResponse(result);
    }

    [HttpGet("dev/deliveries")]
    public async Task<IActionResult> RecentLocalDeliveries([FromQuery] int limit = 25, [FromQuery] string? recipient = null, [FromQuery] string? kind = null)
    {
        if (!AllowLocalDiagnostics())
            return NotFound();

        return OkResponse(await _debug.GetRecentLocalDeliveriesAsync(limit, recipient, kind));
    }

    [HttpGet("dev/password-reset")]
    public async Task<IActionResult> LatestPasswordReset([FromQuery] string? email = null, [FromQuery] string? phoneNumber = null)
    {
        if (!AllowLocalDiagnostics())
            return NotFound();

        var result = await _debug.GetLatestLocalPasswordResetAsync(email, phoneNumber);
        if (result is null)
            return NotFoundResponse("No local password reset delivery found.");

        return OkResponse(result);
    }

    private bool AllowLocalDiagnostics()
        => _env.IsDevelopment() || string.Equals(_env.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);
}
