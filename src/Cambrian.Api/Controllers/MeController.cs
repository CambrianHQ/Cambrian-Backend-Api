using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Current-user self-service endpoints. Exposes the plan-level entitlement matrix
/// the frontend uses to gate features.
/// </summary>
[Route("api/me")]
[Authorize]
public class MeController : BaseController
{
    private readonly IPlanEntitlementService _entitlements;
    private readonly ILogger<MeController> _logger;

    public MeController(IPlanEntitlementService entitlements, ILogger<MeController> logger)
    {
        _entitlements = entitlements;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/me/entitlements — resolve the caller's plan, status, limits and feature flags.
    /// </summary>
    [HttpGet("entitlements")]
    public async Task<IActionResult> Entitlements(CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        var result = await _entitlements.ResolveAsync(userId, ct);
        return OkResponse(result);
    }
}
