using Cambrian.Application.DTOs.ApiKeys;
using Cambrian.Application.Interfaces;
using Cambrian.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Manage personal API keys for programmatic / third-party integrations.
/// Requires JWT or cookie auth — API keys cannot be used to manage other API keys.
/// </summary>
[Route("api/v1/keys")]
[Authorize(Policy = AuthenticationConstants.InteractiveUserPolicy)]
public class ApiKeysController : BaseController
{
    private readonly IApiKeyService _service;

    public ApiKeysController(IApiKeyService service) => _service = service;

    /// <summary>
    /// Create a new API key. The raw key is returned ONCE in the response and never stored —
    /// store it securely immediately.
    /// </summary>
    [Authorize(Policy = "VerifiedEmail")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return ErrorResponse("Key name is required.");

        var userId = GetRequiredUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var (rawKey, meta) = await _service.GenerateAsync(userId, request.Name);
            return CreatedResponse(new
            {
                key       = rawKey,
                prefix    = meta.KeyPrefix,
                name      = meta.Name,
                id        = meta.Id,
                createdAt = meta.CreatedAt,
            }, "API key created. Store it securely — it will not be shown again.");
        }
        catch (ArgumentException ex)
        {
            return ErrorResponse(ex.Message);
        }
    }

    /// <summary>List all active API keys for the authenticated user (prefixes only — no hashes).</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = GetRequiredUserId();
        if (userId is null) return Unauthorized();

        var keys = await _service.ListAsync(userId);
        return OkResponse(keys);
    }

    /// <summary>Revoke (soft-delete) an API key by ID.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var userId = GetRequiredUserId();
        if (userId is null) return Unauthorized();

        var revoked = await _service.RevokeAsync(id, userId);
        if (!revoked) return NotFoundResponse("API key not found or already revoked.");

        return NoContent();
    }
}
