using System.Security.Claims;
using Cambrian.Api.Security;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Public versioned API — creator profiles.
/// </summary>
[ApiController]
[Route("api/v1")]
[EnableRateLimiting("api_key_free")]
[AllowApiKey]
public class CreatorsV1Controller : ControllerBase
{
    private readonly ICreatorIdentityRepository _creators;

    public CreatorsV1Controller(ICreatorIdentityRepository creators) => _creators = creators;

    /// <summary>Get a creator profile by UUID or username slug.</summary>
    [HttpGet("creators/{identifier}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCreator(string identifier)
    {
        // Try UUID first, then fall back to username lookup
        var creator = Guid.TryParse(identifier, out var id)
            ? await _creators.GetByIdAsync(id)
            : await _creators.GetByUsernameAsync(identifier.ToLowerInvariant());

        if (creator is null)
            return NotFound(new { success = false, error = "Creator not found." });

        return Ok(new { success = true, data = creator });
    }
}
