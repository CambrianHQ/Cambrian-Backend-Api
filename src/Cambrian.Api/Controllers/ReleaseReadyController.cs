using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Release Ready mastering: upload+validate → submit (ffmpeg) or approve (Tonn) →
/// poll status → download. A thin HTTP adapter over <see cref="IReleaseReadyService"/>;
/// typed exceptions map to frontend-stable error codes
/// (<c>not_found</c> / <c>invalid_state</c> / <c>insufficient_credits</c> / <c>validation_failed</c>).
/// </summary>
[Route("release-ready")]
[Authorize]
public sealed class ReleaseReadyController : BaseController
{
    private readonly IReleaseReadyService _service;
    private readonly IReleaseCreditService _credits;
    private readonly ILogger<ReleaseReadyController> _logger;

    public ReleaseReadyController(
        IReleaseReadyService service,
        IReleaseCreditService credits,
        ILogger<ReleaseReadyController> logger)
    {
        _service = service;
        _credits = credits;
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

    /// <summary>Buy a one-time Release Ready credit pack. Price is resolved server-side from
    /// the pack id; returns a Stripe checkout URL. Credits are granted by the webhook on
    /// completion. Returns 400 for an unknown pack.</summary>
    [RequireCheckoutEnabled]
    [HttpPost("credits/checkout")]
    public async Task<IActionResult> BuyCredits([FromBody] CreditCheckoutRequest request, CancellationToken ct)
    {
        var userId = GetRequiredUserId()!;
        try
        {
            var result = await _credits.CreateCreditCheckoutAsync(userId, request.Pack, ct);
            return OkResponse(result);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResponse(ex.Message); // 400 — unknown pack / misconfiguration
        }
    }

    /// <summary>Upload + validate a master. Stores the source, runs validation, and
    /// creates a draft job. No credit is spent.</summary>
    [HttpPost("validate")]
    [RequestSizeLimit(200_000_000)] // 200 MB ceiling for an uploaded master
    public async Task<IActionResult> Validate([FromForm] ReleaseReadyValidateRequest request, CancellationToken ct)
    {
        if (request.Audio is null || request.Audio.Length == 0)
            return ReleaseReadyBadRequest(
                ReleaseReadyErrorCodes.ValidationFailed,
                "An audio file is required.",
                new[] { new ReleaseReadyError { Code = ReleaseReadyErrorCodes.InvalidAudio, Message = "An audio file is required.", Field = "audio" } });

        var userId = GetRequiredUserId()!;

        // Buffer the uploaded files into seekable memory streams for validation + storage.
        await using var audioBuffer = await BufferAsync(request.Audio, ct);
        await using var artworkBuffer = request.Artwork is { Length: > 0 } ? await BufferAsync(request.Artwork, ct) : null;

        var input = new ReleaseReadyUploadInput
        {
            UserId = userId,
            Audio = audioBuffer,
            AudioFileName = request.Audio.FileName,
            Artwork = artworkBuffer,
            ArtworkFileName = request.Artwork?.FileName,
            TrackId = request.TrackId,
            AiGenerated = request.AiGenerated,
            AiDisclosure = request.AiDisclosure,
            TargetLufs = request.TargetLufs,
        };

        try
        {
            var result = await _service.ValidateAndCreateAsync(input, ct);
            return OkResponse(result);
        }
        catch (ReleaseReadyValidationException ex)
        {
            return BadRequest(new ReleaseReadyErrorResponse
            {
                Error = new ReleaseReadyError
                {
                    Code = ReleaseReadyErrorCodes.ValidationFailed,
                    Message = ex.Message,
                    Details = new { errorCount = ex.Errors.Count },
                },
                Errors = ex.Errors,
                Validation = ex.Validation,
            });
        }
        catch (InvalidOperationException ex)
        {
            return ReleaseReadyBadRequest(ReleaseReadyErrorCodes.ValidationFailed, ex.Message);
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
        catch (InsufficientCreditsException ex) { return ReleaseReadyForbidden(ReleaseReadyErrorCodes.InsufficientCredits, ex.Message); }
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
        catch (InsufficientCreditsException ex) { return ReleaseReadyForbidden(ReleaseReadyErrorCodes.InsufficientCredits, ex.Message); }
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

    private IActionResult ReleaseReadyBadRequest(
        string code,
        string message,
        IReadOnlyList<ReleaseReadyError>? errors = null) =>
        BadRequest(new ReleaseReadyErrorResponse
        {
            Error = new ReleaseReadyError { Code = code, Message = message },
            Errors = errors ?? new[] { new ReleaseReadyError { Code = code, Message = message } },
        });

    private IActionResult ReleaseReadyForbidden(string code, string message) =>
        StatusCode(403, new ReleaseReadyErrorResponse
        {
            Error = new ReleaseReadyError { Code = code, Message = message },
            Errors = new[] { new ReleaseReadyError { Code = code, Message = message } },
        });
}
