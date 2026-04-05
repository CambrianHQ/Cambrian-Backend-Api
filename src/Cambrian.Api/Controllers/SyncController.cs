using Cambrian.Application.DTOs.Sync;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("sync")]
public class SyncController : BaseController
{
    private readonly ISyncService _syncService;

    public SyncController(ISyncService syncService)
    {
        _syncService = syncService;
    }

    /// <summary>Create a new sync licensing brief.</summary>
    [HttpPost("briefs")]
    [Authorize]
    public async Task<IActionResult> CreateBrief([FromBody] CreateSyncBriefRequest request)
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var result = await _syncService.CreateBriefAsync(userId, request);
        return CreatedResponse(result, "Sync brief created.");
    }

    /// <summary>List open sync briefs (paginated, filterable by genre/budget).</summary>
    [HttpGet("briefs")]
    [AllowAnonymous]
    public async Task<IActionResult> ListBriefs(
        [FromQuery] string? genre,
        [FromQuery] decimal? minBudget,
        [FromQuery] decimal? maxBudget,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await _syncService.ListBriefsAsync(genre, minBudget, maxBudget, page, pageSize);
        return OkResponse(result);
    }

    /// <summary>Get a sync brief's details and submission count.</summary>
    [HttpGet("briefs/{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBrief(Guid id)
    {
        var result = await _syncService.GetBriefAsync(id);
        if (result is null) return NotFoundResponse("Sync brief not found.");
        return OkResponse(result);
    }

    /// <summary>Submit a track to a sync brief (creator only).</summary>
    [HttpPost("briefs/{id:guid}/submit")]
    [Authorize(Roles = "Creator")]
    public async Task<IActionResult> SubmitToBrief(Guid id, [FromBody] SubmitToSyncBriefRequest request)
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var result = await _syncService.SubmitToBriefAsync(id, userId, request);
        if (result is null) return NotFoundResponse("Brief not found or not open for submissions.");
        return CreatedResponse(result, "Submission received.");
    }

    /// <summary>Select a winning submission (buyer who owns the brief).</summary>
    [HttpPut("briefs/{id:guid}/select/{submissionId:guid}")]
    [Authorize]
    public async Task<IActionResult> SelectSubmission(Guid id, Guid submissionId)
    {
        var userId = GetRequiredUserId();
        if (userId is null) return ErrorResponse("User not authenticated.");

        var success = await _syncService.SelectSubmissionAsync(id, submissionId, userId);
        if (!success) return ErrorResponse("Brief not found, not owned by you, or submission not found.");
        return MessageResponse("Submission selected. Brief marked as filled.");
    }
}
