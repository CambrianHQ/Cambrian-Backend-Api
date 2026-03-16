using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("feature-flags")]
public class FeatureFlagsController : BaseController
{
    private readonly IFeatureFlagRepository _flags;

    public FeatureFlagsController(IFeatureFlagRepository flags)
    {
        _flags = flags;
    }

    /// <summary>
    /// Check whether a feature flag is enabled for the current user.
    /// </summary>
    [Authorize]
    [HttpGet("check/{name}")]
    public async Task<IActionResult> Check(string name)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var enabled = await _flags.IsEnabledAsync(name, userId);
        return OkResponse(new { name, enabled });
    }

    /// <summary>
    /// Admin: list all feature flags.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var flags = await _flags.GetAllAsync();
        var result = new List<object>();
        foreach (var f in flags)
        {
            result.Add(new
            {
                id = f.Id.ToString(),
                name = f.Name,
                enabled = f.Enabled,
                rolloutPercentage = f.RolloutPercentage,
                createdAt = f.CreatedAt,
                updatedAt = f.UpdatedAt,
            });
        }
        return OkResponse(result);
    }

    /// <summary>
    /// Admin: create or update a feature flag.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPut("{name}")]
    public async Task<IActionResult> Upsert(string name, [FromBody] UpsertFlagRequest body)
    {
        var flag = await _flags.UpsertAsync(name, body.Enabled, body.RolloutPercentage);
        return OkResponse(new
        {
            id = flag.Id.ToString(),
            name = flag.Name,
            enabled = flag.Enabled,
            rolloutPercentage = flag.RolloutPercentage,
            updatedAt = flag.UpdatedAt,
        });
    }

    /// <summary>
    /// Admin: delete a feature flag.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name)
    {
        await _flags.DeleteAsync(name);
        return MessageResponse($"Feature flag '{name}' deleted.");
    }

    public class UpsertFlagRequest
    {
        public bool Enabled { get; set; }
        public int RolloutPercentage { get; set; } = 100;
    }
}
