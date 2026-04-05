using Cambrian.Application.DTOs.Referrals;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("referral")]
public class ReferralController : BaseController
{
    private readonly IReferralService _referralService;

    public ReferralController(IReferralService referralService)
    {
        _referralService = referralService;
    }

    /// <summary>Get or generate the current user's referral code.</summary>
    [HttpGet("code")]
    [Authorize]
    public async Task<IActionResult> GetReferralCode()
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var result = await _referralService.GetOrCreateReferralCodeAsync(userId);
        return OkResponse(result);
    }

    /// <summary>Get referral stats for the current user.</summary>
    [HttpGet("stats")]
    [Authorize]
    public async Task<IActionResult> GetReferralStats()
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var result = await _referralService.GetReferralStatsAsync(userId);
        return OkResponse(result);
    }

    /// <summary>Track a referral code for attribution.</summary>
    [HttpPost("track")]
    [AllowAnonymous]
    public async Task<IActionResult> TrackReferral([FromBody] TrackReferralRequest request)
    {
        var found = await _referralService.TrackReferralAsync(request.ReferralCode);
        if (!found) return NotFoundResponse("Invalid referral code.");
        return MessageResponse("Referral tracked.");
    }
}
