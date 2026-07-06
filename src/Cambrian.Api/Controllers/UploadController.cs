using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("")]
[EnableRateLimiting("auth")]
public class UploadController : BaseController
{
    private readonly IUploadService _upload;
    private readonly IObjectStorage _storage;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UploadController> _logger;

    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private const long MaxImageSize = 10 * 1024 * 1024; // 10 MB

    public UploadController(IUploadService upload, IObjectStorage storage, IMemoryCache cache, ILogger<UploadController> logger)
    {
        _upload = upload;
        _storage = storage;
        _cache = cache;
        _logger = logger;
    }

    [Authorize(Policy = "CanUploadTrack")]
    [RequireCreatorTier]
    [RequireUsername]
    [HttpPost("upload")]
    [RequestSizeLimit(150 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] UploadTrackRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("EVENT: UploadStarted userId:{UserId} title:{Title}", userId, request.Title);
        request.CreatorId = userId;

        var verificationGate = RequireVerifiedEmailToPublish(request.SaveAsDraft);
        if (verificationGate is not null) return verificationGate;

        try
        {
            var result = await _upload.Upload(request);
            if (!string.IsNullOrWhiteSpace(result.CoverArtUrl))
                result.CoverArtUrl = ResolveImageUrl(result.CoverArtUrl);
            _logger.LogInformation("EVENT: UploadCompleted userId:{UserId} trackId:{TrackId} title:{Title}", userId, result, request.Title);

            // Invalidate catalog cache so the new track appears immediately
            if (_cache is MemoryCache mc)
                mc.Compact(1.0);

            return CreatedResponse(result, "Track uploaded successfully.");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "EVENT: UploadRejected userId:{UserId} title:{Title}", userId, request.Title);
            Cambrian.Application.Observability.CambrianMetrics.UploadFailed.Add(1);
            return ErrorResponse(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "EVENT: UploadBlocked userId:{UserId} title:{Title}", userId, request.Title);
            Cambrian.Application.Observability.CambrianMetrics.UploadFailed.Add(1);
            return ErrorResponse(ex.Message);
        }
    }

    // ───── POST /api/tracks — spec-canonical track creation ─────
    // Alias of POST /upload that enforces the plan track limit and returns a typed
    // 402 + UPGRADE_REQUIRED code the frontend can detect to launch the upgrade flow.

    [Authorize(Policy = "CanUploadTrack")]
    [RequireCreatorTier]
    [RequireUsername]
    [HttpPost("/api/tracks")]
    [RequestSizeLimit(150 * 1024 * 1024)]
    public async Task<IActionResult> CreateTrack([FromForm] UploadTrackRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("EVENT: TrackCreateStarted userId:{UserId} title:{Title}", userId, request.Title);
        request.CreatorId = userId;

        var verificationGate = RequireVerifiedEmailToPublish(request.SaveAsDraft);
        if (verificationGate is not null) return verificationGate;

        try
        {
            var result = await _upload.Upload(request);
            if (!string.IsNullOrWhiteSpace(result.CoverArtUrl))
                result.CoverArtUrl = ResolveImageUrl(result.CoverArtUrl);

            if (_cache is MemoryCache mc)
                mc.Compact(1.0);

            _logger.LogInformation("EVENT: TrackCreateCompleted userId:{UserId} title:{Title}", userId, request.Title);
            return CreatedResponse(result, "Track uploaded successfully.");
        }
        catch (UpgradeRequiredException ex)
        {
            _logger.LogWarning(ex, "EVENT: TrackCreateUpgradeRequired userId:{UserId} title:{Title}", userId, request.Title);
            Cambrian.Application.Observability.CambrianMetrics.UploadFailed.Add(1);
            return UpgradeRequiredResponse(ex.Message, ex.Code);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "EVENT: TrackCreateRejected userId:{UserId} title:{Title}", userId, request.Title);
            Cambrian.Application.Observability.CambrianMetrics.UploadFailed.Add(1);
            return ErrorResponse(ex.Message);
        }
    }

    // Drafts (SaveAsDraft=true, Track.Visibility stays "hidden") never require a
    // verified email — only a track that goes public on upload does. Mirrors the
    // structured body VerifiedEmailForbiddenResponseMiddleware produces for the
    // other VerifiedEmail-gated endpoints, since this check can't run at the
    // attribute level (SaveAsDraft isn't known until the form body is bound).
    private IActionResult? RequireVerifiedEmailToPublish(bool? saveAsDraft)
    {
        if (saveAsDraft == true) return null;
        if (User.HasClaim(c => c.Type == "email_verified" && c.Value == "true")) return null;

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = new
            {
                code = "email_not_verified",
                message = "Verify your email before publishing a track. Save it as a draft in the meantime.",
            }
        });
    }

    // ───── POST /uploads/image — generic image upload ─────
    // Upload an image, receive back its public URL.
    // Recommended pattern: upload → get URL → save URL in profile via PATCH /users/me.

    [Authorize]
    [HttpPost("uploads/image")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return ErrorResponse("No file provided.");

        if (file.Length > MaxImageSize)
            return ErrorResponse("File too large. Maximum size is 10 MB.");

        var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant() ?? "";
        if (!AllowedImageExtensions.Contains(ext))
            return ErrorResponse("Invalid file type. Accepted: jpg, jpeg, png, webp.");

        var key = $"images/{Guid.NewGuid()}{ext}";
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".webp"           => "image/webp",
            _                 => "application/octet-stream"
        };

        await using var stream = file.OpenReadStream();
        await _storage.UploadAsync(stream, key, contentType);
        var url = _storage.GenerateSignedUrl(key);

        return OkResponse(new { url });
    }
}
