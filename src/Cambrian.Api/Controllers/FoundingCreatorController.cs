using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("api")]
public class FoundingCreatorController : BaseController
{
    private readonly IFoundingCreatorService _foundingCreatorService;

    public FoundingCreatorController(IFoundingCreatorService foundingCreatorService)
    {
        _foundingCreatorService = foundingCreatorService;
    }

    /// <summary>Enroll a user as a Founding Creator (admin only).</summary>
    [HttpPost("admin/founding-creator/{userId}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> EnrollFoundingCreator(string userId)
    {
        var result = await _foundingCreatorService.EnrollFoundingCreatorAsync(userId);
        if (result is null) return NotFoundResponse("User not found.");
        return OkResponse(result, "User enrolled as Founding Creator.");
    }

    /// <summary>List all founding creators (admin only).</summary>
    [HttpGet("admin/founding-creators")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetFoundingCreators()
    {
        var result = await _foundingCreatorService.GetFoundingCreatorsAsync();
        return OkResponse(result);
    }

    /// <summary>Get current user's founding creator status.</summary>
    [HttpGet("creator/founding-status")]
    [Authorize(Roles = "Creator")]
    public async Task<IActionResult> GetFoundingStatus()
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var result = await _foundingCreatorService.GetFoundingStatusAsync(userId);
        if (result is null) return NotFoundResponse("User not found.");
        return OkResponse(result);
    }
}
