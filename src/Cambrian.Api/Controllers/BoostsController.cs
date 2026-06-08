using Cambrian.Application.DTOs.Community;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Boost (upvote) endpoints for the creator-community layer.
/// Boosting requires a logged-in, email-verified account and is rate-limited.
/// Reading the boost status is anonymous-friendly (HasBoosted = false when
/// unauthenticated).
/// </summary>
[Route("tracks/{trackId}/boost")]
[EnableRateLimiting("community")]
public class BoostsController : BaseController
{
    private readonly ITrackBoostService _boosts;

    public BoostsController(ITrackBoostService boosts)
    {
        _boosts = boosts;
    }

    /// <summary>Boost a public track. One per user; self-boosts rejected.</summary>
    [HttpPost]
    [Authorize(Policy = "VerifiedEmail")]
    public Task<IActionResult> Boost(string trackId) =>
        Run(() => _boosts.BoostAsync(User, trackId));

    /// <summary>Remove the current user's boost (idempotent).</summary>
    [HttpDelete]
    [Authorize(Policy = "VerifiedEmail")]
    public Task<IActionResult> Unboost(string trackId) =>
        Run(() => _boosts.UnboostAsync(User, trackId));

    /// <summary>Current boost count + whether the caller has boosted the track.</summary>
    [HttpGet]
    [AllowAnonymous]
    public Task<IActionResult> Status(string trackId) =>
        Run(() => _boosts.GetStatusAsync(User, trackId));

    private async Task<IActionResult> Run(Func<Task<BoostStatusResponse>> action)
    {
        try
        {
            return OkResponse(await action());
        }
        catch (InvalidOperationException ex)
        {
            return ForbiddenResponse(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFoundResponse("Track not found.");
        }
        catch (FormatException)
        {
            return ErrorResponse("Track id must be a valid identifier.");
        }
    }
}
