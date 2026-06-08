using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Release Ready mastering: upload+validate → submit (ffmpeg) or approve (Tonn) →
/// poll status → download. A thin HTTP adapter over <see cref="IReleaseReadyService"/>;
/// typed exceptions map to the envelope error codes
/// (<c>not_found</c> / <c>invalid_state</c> / <c>insufficient_credits</c> / <c>validation</c>).
/// </summary>
[Route("release-ready")]
[Authorize]
public sealed class ReleaseReadyController : BaseController
{
    private readonly IReleaseReadyService _service;
    private readonly ILogger<ReleaseReadyController> _logger;

    public ReleaseReadyController(IReleaseReadyService service, ILogger<ReleaseReadyController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>Monthly Release Ready credit balance for the signed-in creator.</summary>
    [HttpGet("credits")]
    public async Task<IActionResult> Credits(CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        var status = await _service.GetCreditsAsync(userId, ct);
        return OkResponse(status);
    }

    /// <summary>Upload + validate a master. Stores the source, runs validation, and
    /// creates a draft job. No credit is spent.</summary>
    [HttpPost("validate")]
    [RequestSizeLimit(200_000_000)] // 200 MB ceiling for an uploaded master
    public async Task<IActionResult> Validate(
        [FromForm] IFormFile? audio,
        [FromForm] IFormFile? artwork,
        [FromForm] Guid? trackId,
        [FromForm] bool aiGenerated,
        [FromForm] string? aiDisclosure,
        [FromForm] double? targetLufs,
        CancellationToken ct)
    {
        if (audio is null || audio.Length == 0)
            return ErrorResponse("An audio file is required.");

        var userId = GetRequiredUserId()!;

        // Buffer the uploaded files into seekable memory streams for validation + storage.
        await using var audioBuffer = await BufferAsync(audio, ct);
        await using var artworkBuffer = artwork is { Length: > 0 } ? await BufferAsync(artwork, ct) : null;

        var input = new ReleaseReadyUploadInput
        {
            UserId = userId,
            Audio = audioBuffer,
            AudioFileName = audio.FileName,
            Artwork = artworkBuffer,
            ArtworkFileName = artwork?.FileName,
            TrackId = trackId,
            AiGenerated = aiGenerated,
            AiDisclosure = aiDisclosure,
            TargetLufs = targetLufs,
        };

        try
        {
            var result = await _service.ValidateAndCreateAsync(input, ct);
            return OkResponse(result);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResponse(ex.Message); // 400 validation
        }
    }

    /// <summary>Start mastering. Charges a credit for one-shot engines (ffmpeg).</summary>
    [HttpPost("jobs/{id}/submit")]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        try
        {
            var job = await _service.SubmitAsync(id, userId, ct);
            return OkResponse(job);
        }
        catch (KeyNotFoundException) { return NotFoundResponse("Mastering job not found."); }
        catch (InsufficientCreditsException ex) { return ForbiddenResponse(ex.Message); }
        catch (InvalidOperationException ex) { return ConflictResponse(ex.Message); }
    }

    /// <summary>Approve a preview (preview engines only). Charges a credit and finalizes.</summary>
    [HttpPost("jobs/{id}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        try
        {
            var job = await _service.ApproveAsync(id, userId, ct);
            return OkResponse(job);
        }
        catch (KeyNotFoundException) { return NotFoundResponse("Mastering job not found."); }
        catch (InsufficientCreditsException ex) { return ForbiddenResponse(ex.Message); }
        catch (InvalidOperationException ex) { return ConflictResponse(ex.Message); }
    }

    /// <summary>Poll a single job's full state.</summary>
    [HttpGet("jobs/{id}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        var job = await _service.GetJobAsync(id, userId, ct);
        return job is null ? NotFoundResponse("Mastering job not found.") : OkResponse(job);
    }

    /// <summary>List the caller's recent jobs (newest first).</summary>
    [HttpGet("jobs")]
    public async Task<IActionResult> List([FromQuery] int take = 20, CancellationToken ct = default)
    {
        var userId = GetRequiredUserId()!;
        var jobs = await _service.ListJobsAsync(userId, take, ct);
        return OkResponse(jobs);
    }

    /// <summary>Download a mastered asset — a 302 redirect to a signed URL (S3) or a
    /// streamed body (local), mirroring <c>DownloadController</c>.</summary>
    [HttpGet("jobs/{id}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string format = "wav", CancellationToken ct = default)
    {
        var userId = GetRequiredUserId()!;
        var download = await _service.GetDownloadAsync(id, userId, format, ct);
        if (download is null)
            return NotFoundResponse("Mastered asset not found.");

        if (!string.IsNullOrWhiteSpace(download.SignedUrl))
            return Redirect(download.SignedUrl!);

        if (download.Content is not null)
        {
            Response.Headers["Cache-Control"] = "private, no-store";
            return File(download.Content, download.ContentType, download.FileName, enableRangeProcessing: true);
        }

        return NotFoundResponse("Mastered asset not found.");
    }

    private static async Task<MemoryStream> BufferAsync(IFormFile file, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }
}
