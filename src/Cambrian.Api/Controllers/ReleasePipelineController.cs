using Cambrian.Api.Common;
using Cambrian.Application.DTOs.Readiness;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Release pipeline surface (contract: docs/api-contracts.md):
/// readiness scoring, release-ready job start, and job status polling.
/// Thin HTTP adapter over <see cref="ITrackReadinessService"/> and
/// <see cref="ITrackReleasePipelineService"/>.
/// </summary>
[Route("api")]
[Authorize]
public sealed class ReleasePipelineController : BaseController
{
    private readonly ITrackReadinessService _readiness;
    private readonly ITrackReleasePipelineService _pipeline;
    private readonly ITrackRepository _tracks;
    private readonly ILogger<ReleasePipelineController> _logger;

    public ReleasePipelineController(
        ITrackReadinessService readiness,
        ITrackReleasePipelineService pipeline,
        ITrackRepository tracks,
        ILogger<ReleasePipelineController> logger)
    {
        _readiness = readiness;
        _pipeline = pipeline;
        _tracks = tracks;
        _logger = logger;
    }

    /// <summary>Weighted release-readiness score for the caller's track.</summary>
    [HttpGet("tracks/{id:guid}/readiness")]
    public async Task<IActionResult> Readiness(Guid id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;

        // Owner-scoped: 404 (not 403) so track existence isn't leaked.
        var track = await _tracks.GetByIdAsync(id);
        if (track is null || !string.Equals(track.CreatorId, userId, StringComparison.Ordinal))
            return NotFoundResponse("Track not found.");

        var result = await _readiness.GetAsync(id, ct);
        return result is null ? NotFoundResponse("Track not found.") : OkResponse(result);
    }

    /// <summary>Legacy un-prefixed readiness path (residue F7) — permanent 308 to the
    /// canonical <c>/api/tracks/{id}/readiness</c>. Do not add new callers.</summary>
    [AllowAnonymous]
    [HttpGet("/tracks/{id:guid}/readiness")]
    public IActionResult LegacyReadiness(Guid id) =>
        RedirectPermanentPreserveMethod($"/api/tracks/{id}/readiness");

    /// <summary>Start a Release Ready pipeline job for the track. 202 with the job id;
    /// 402 when the monthly credit allowance is exhausted.</summary>
    [HttpPost("tracks/{id:guid}/release-ready")]
    public async Task<IActionResult> ReleaseReady(Guid id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        try
        {
            var result = await _pipeline.StartAsync(id, userId, ct);

            if (!result.Created)
                return OkResponse(result, result.Warning);

            return Accepted(ApiResponse<StartReleaseJobResult>.Ok(result));
        }
        catch (KeyNotFoundException) { return NotFoundResponse("Track not found."); }
        catch (InsufficientCreditsException ex)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, ApiResponse.Fail(ex.Message));
        }
        catch (InvalidOperationException ex) { return ConflictResponse(ex.Message); }
    }

    /// <summary>Poll a release job: status, current stage, persisted stage history, signed artifacts.</summary>
    [HttpGet("jobs/{id:guid}")]
    public async Task<IActionResult> Job(Guid id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        var job = await _pipeline.GetJobAsync(id, userId, ct);
        return job is null ? NotFoundResponse("Job not found.") : OkResponse(job);
    }
}
