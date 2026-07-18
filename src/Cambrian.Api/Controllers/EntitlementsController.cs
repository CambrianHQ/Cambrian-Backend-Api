using Cambrian.Application.DTOs.Entitlements;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Unified access-control surface. Admin grants + user-facing access checks
/// both live here. Individual features (downloads, streams, exclusive
/// content) call <see cref="IEntitlementService.HasAccessAsync"/> directly
/// from their own controllers — this endpoint is for diagnostic / library
/// views.
/// </summary>
[ApiController]
[Route("api/entitlements")]
public sealed class EntitlementsController : BaseController
{
    private readonly IEntitlementService _entitlements;

    public EntitlementsController(IEntitlementService entitlements)
    {
        _entitlements = entitlements;
    }

    /// <summary>
    /// Admin grant. Mints an entitlement row for any user on any resource.
    /// Gated by <c>[Authorize(Roles="Admin")]</c> — feature services that
    /// need to grant entitlements inline (tips, subscriptions) call the
    /// service directly, not this endpoint.
    /// </summary>
    [HttpPost("grant")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Grant([FromBody] GrantEntitlementRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return ErrorResponse("Invalid request payload.");

        try
        {
            var granted = await _entitlements.GrantAsync(
                req.UserId,
                req.ResourceType,
                req.ResourceId,
                req.AccessLevel,
                req.SourceType,
                req.SourceId,
                req.ExpiresAt,
                ct);

            return CreatedResponse(EntitlementDto.From(granted), "Entitlement granted.");
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(ex.Message);
        }
    }

    /// <summary>
    /// Admin revoke. Soft-deletes the row so audit history is preserved and
    /// subsequent access checks deny. Returns 404 when the id is unknown or
    /// already revoked.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] RevokeEntitlementRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return ErrorResponse("A non-empty reason is required.");

        var revoked = await _entitlements.RevokeAsync(id, req.Reason, ct);
        if (!revoked)
            return NotFoundResponse($"Entitlement '{id}' not found or already revoked.");

        return MessageResponse("Entitlement revoked.");
    }

    /// <summary>
    /// List the caller's own entitlements. Optional <paramref name="resourceType"/>
    /// filter narrows to e.g. all Tracks. Revoked rows are hidden unless
    /// <paramref name="includeRevoked"/> is true.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Mine(
        [FromQuery] EntitlementResourceType? resourceType = null,
        [FromQuery] bool includeRevoked = false,
        CancellationToken ct = default)
    {
        var userId = GetRequiredUserId();
        if (string.IsNullOrEmpty(userId))
            return ErrorResponse("Unable to resolve user identity.");

        var rows = await _entitlements.GetForUserAsync(userId, resourceType, includeRevoked, ct);
        var dtos = rows.ConvertAll(EntitlementDto.From);
        return OkResponse(dtos);
    }

    /// <summary>
    /// Boolean access check for the caller on a specific resource. Used by
    /// frontends deciding whether to show a Download / Play / Unlock button
    /// without paging the full entitlement list.
    /// </summary>
    [HttpGet("access")]
    [Authorize]
    public async Task<IActionResult> CheckAccess(
        [FromQuery] EntitlementResourceType resourceType,
        [FromQuery] string resourceId,
        [FromQuery] EntitlementAccessLevel level = EntitlementAccessLevel.Stream,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            return ErrorResponse("resourceId is required.");

        var userId = GetRequiredUserId();
        if (string.IsNullOrEmpty(userId))
            return ErrorResponse("Unable to resolve user identity.");

        var has = await _entitlements.HasAccessAsync(userId, resourceType, resourceId, level, ct);
        return OkResponse(new AccessCheckResponse
        {
            HasAccess = has,
            ResourceType = resourceType,
            ResourceId = resourceId,
            RequiredLevel = level,
        });
    }
}
