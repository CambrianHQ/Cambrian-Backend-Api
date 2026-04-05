using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("analytics/pricing")]
public class PricingIntelligenceController : BaseController
{
    private readonly IPricingIntelligenceService _pricingService;

    public PricingIntelligenceController(IPricingIntelligenceService pricingService)
    {
        _pricingService = pricingService;
    }

    /// <summary>Get pricing intelligence for a specific genre.</summary>
    [HttpGet("{genre}")]
    [Authorize(Roles = "Creator,Admin")]
    public async Task<IActionResult> GetGenrePricing(string genre)
    {
        var result = await _pricingService.GetGenrePricingAsync(genre);
        if (result is null) return NotFoundResponse("No tracks found for this genre.");
        return OkResponse(result);
    }

    /// <summary>Get the current creator's pricing position across their genres.</summary>
    [HttpGet("my-position")]
    [Authorize(Roles = "Creator")]
    public async Task<IActionResult> GetMyPosition()
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var result = await _pricingService.GetCreatorPositionAsync(userId);
        return OkResponse(result);
    }
}
