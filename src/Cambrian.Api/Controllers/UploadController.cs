using System.Security.Claims;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Interfaces.V1;
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
    private readonly IIdempotencyStore? _idempotency;
    private static readonly JsonSerializerOptions IdempotencyJson = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private const long MaxImageSize = 10 * 1024 * 1024; // 10 MB

    public UploadController(IUploadService upload, IObjectStorage storage, IMemoryCache cache, ILogger<UploadController> logger, IIdempotencyStore? idempotency = null)
    {
        _upload = upload;
        _storage = storage;
        _cache = cache;
        _logger = logger;
        _idempotency = idempotency;
    }

    [Authorize(Policy = "CanUploadTrack")]
    [RequireCreatorTier]
    [RequireUsername]
    [HttpPost("upload")]
    [RequestSizeLimit(150 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] UploadTrackRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Cambrian.Application.Observability.CambrianMetrics.UploadStarted.Add(1);
        _logger.LogInformation("EVENT: upload_started userId:{UserId} fileName:{FileName}", userId, request.Audio?.FileName);
        request.CreatorId = userId;

        var verificationGate = RequireVerifiedEmailToPublish(request.SaveAsDraft);
        if (verificationGate is not null)
        {
            Cambrian.Application.Observability.CambrianMetrics.UploadFailed.Add(1);
            _logger.LogWarning("EVENT: upload_failed category:authorization code:email_not_verified userId:{UserId}", userId);
            return verificationGate;
        }

        var (replay, idempotencyClaimed) = await BeginIdempotentRequestAsync(
            userId, "POST /upload", ComputeRequestHash(new[] { request }));
        if (replay is not null) return replay;
        var idempotencyCompleted = false;

        try
        {
            var result = await _upload.Upload(request);
            if (!string.IsNullOrWhiteSpace(result.CoverArtUrl))
                result.CoverArtUrl = ResolveImageUrl(result.CoverArtUrl);
            _logger.LogInformation("EVENT: upload_completed userId:{UserId} trackId:{TrackId}", userId, result.TrackId);

            // Invalidate catalog cache so the new track appears immediately
            if (_cache is MemoryCache mc)
                mc.Compact(1.0);

            var body = ApiResponse<UploadTrackResponse>.Ok(result, "Track uploaded successfully.");
            await SaveIdempotentResponseAsync(userId, "POST /upload", StatusCodes.Status201Created, body);
            idempotencyCompleted = true;
            return StatusCode(StatusCodes.Status201Created, body);
        }
        catch (ArgumentException ex)
        {
            return StructuredUploadFailure(ex, request.Audio?.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return StructuredUploadFailure(ex, request.Audio?.FileName);
        }
        catch (UploadOperationException ex)
        {
            return StructuredUploadFailure(ex, request.Audio?.FileName);
        }
        finally
        {
            if (idempotencyClaimed && !idempotencyCompleted)
                await MarkIdempotentRequestFailedAsync(userId, "POST /upload");
        }
    }

    /// <summary>
    /// Upload one to ten tracks in a single multipart request. Every item is
    /// isolated and returns its own result; one invalid or failed file never
    /// turns the remaining results into a false all-or-nothing success.
    /// </summary>
    [Authorize(Policy = "CanUploadTrack")]
    [RequireCreatorTier]
    [RequireUsername]
    [HttpPost("uploads/batch")]
    [RequestSizeLimit(1024L * 1024L * 1024L)]
    public async Task<IActionResult> UploadBatch([FromForm] BatchUploadRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (request.Tracks.Count is < 1 or > 10)
            return BadRequest(new { success = false, error = new { code = "invalid_batch_size", message = "A batch must contain between 1 and 10 tracks." } });

        var (replay, idempotencyClaimed) = await BeginIdempotentRequestAsync(
            userId, "POST /uploads/batch", ComputeRequestHash(request.Tracks));
        if (replay is not null) return replay;
        var idempotencyCompleted = false;

        try
        {
        var results = new List<BatchUploadTrackResult>(request.Tracks.Count);
        for (var index = 0; index < request.Tracks.Count; index++)
        {
            var item = request.Tracks[index];
            item.CreatorId = userId;
            var fileName = item.Audio?.FileName;
            Cambrian.Application.Observability.CambrianMetrics.UploadStarted.Add(1);
            _logger.LogInformation("EVENT: upload_started userId:{UserId} batchIndex:{BatchIndex} fileName:{FileName}", userId, index, fileName);

            if (RequireVerifiedEmailToPublish(item.SaveAsDraft) is not null)
            {
                Cambrian.Application.Observability.CambrianMetrics.UploadFailed.Add(1);
                results.Add(BatchUploadTrackResult.Failed(index, fileName, "email_not_verified",
                    "Verify your email before publishing a track. Save it as a draft in the meantime.", "authorization"));
                continue;
            }

            try
            {
                var uploaded = await _upload.Upload(item);
                if (!string.IsNullOrWhiteSpace(uploaded.CoverArtUrl))
                    uploaded.CoverArtUrl = ResolveImageUrl(uploaded.CoverArtUrl);
                results.Add(BatchUploadTrackResult.Succeeded(index, fileName, uploaded));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UpgradeRequiredException or UploadOperationException)
            {
                var failure = DescribeUploadFailure(ex);
                results.Add(BatchUploadTrackResult.Failed(index, fileName, failure.Code, failure.Message, failure.Category));
            }
        }

        var succeeded = results.Count(x => x.Success);
        var failed = results.Count - succeeded;
        Cambrian.Application.Observability.CambrianMetrics.BatchUploadCompleted.Add(1);
        _logger.LogInformation("EVENT: batch_upload_completed userId:{UserId} total:{Total} succeeded:{Succeeded} failed:{Failed}",
            userId, results.Count, succeeded, failed);

        if (_cache is MemoryCache mc && succeeded > 0)
            mc.Compact(1.0);

        var data = new { total = results.Count, succeeded, failed, results };
        var body = ApiResponse<object>.Ok(data);
        await SaveIdempotentResponseAsync(userId, "POST /uploads/batch", StatusCodes.Status200OK, body);
        idempotencyCompleted = true;
        return Ok(body);
        }
        finally
        {
            if (idempotencyClaimed && !idempotencyCompleted)
                await MarkIdempotentRequestFailedAsync(userId, "POST /uploads/batch");
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
            return UpgradeRequiredResponse(ex.Message, ex.Code);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "EVENT: TrackCreateRejected userId:{UserId} title:{Title}", userId, request.Title);
            return StructuredUploadFailure(ex, request.Audio?.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return StructuredUploadFailure(ex, request.Audio?.FileName);
        }
        catch (UploadOperationException ex)
        {
            return StructuredUploadFailure(ex, request.Audio?.FileName);
        }
    }

    private IActionResult StructuredUploadFailure(Exception ex, string? fileName)
    {
        var failure = DescribeUploadFailure(ex);
        _logger.LogWarning(ex,
            "EVENT: upload_failed category:{Category} code:{Code} userId:{UserId} fileName:{FileName} correlationId:{CorrelationId}",
            failure.Category, failure.Code, User.FindFirstValue(ClaimTypes.NameIdentifier), fileName, HttpContext.TraceIdentifier);
        var body = new
        {
            success = false,
            error = new
            {
                code = failure.Code,
                message = failure.Message,
                fileName,
                category = failure.Category,
                correlationId = HttpContext.TraceIdentifier,
            }
        };
        var statusCode = ex switch
        {
            UploadOperationException => StatusCodes.Status503ServiceUnavailable,
            DuplicateUploadException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return StatusCode(statusCode, body);
    }

    private static UploadFailure DescribeUploadFailure(Exception ex)
    {
        if (ex is UploadOperationException operation)
            return new(operation.Code, operation.Message, operation.Category);
        if (ex is DuplicateUploadException duplicate)
            return new("duplicate_audio_detected", duplicate.Message, "duplicate");
        if (ex is UpgradeRequiredException upgrade)
            return new(upgrade.Code.ToLowerInvariant(), upgrade.Message, "entitlement");
        if (ex is InvalidOperationException)
            return new("upload_conflict", ex.Message, "persistence");

        var message = ex.Message;
        var code = message switch
        {
            var x when x.Contains("Audio file is required", StringComparison.OrdinalIgnoreCase) => "audio_required",
            var x when x.Contains("File type", StringComparison.OrdinalIgnoreCase) => "invalid_file_type",
            var x when x.Contains("MIME type", StringComparison.OrdinalIgnoreCase) => "invalid_mime_type",
            var x when x.Contains("File size", StringComparison.OrdinalIgnoreCase) => "file_too_large",
            var x when x.Contains("File content", StringComparison.OrdinalIgnoreCase) => "invalid_file_content",
            var x when x.Contains("Album", StringComparison.OrdinalIgnoreCase) || x.Contains("CollectionId", StringComparison.OrdinalIgnoreCase) => "album_assignment_invalid",
            _ => "validation_failed",
        };
        return new(code, message, "validation");
    }

    private sealed record UploadFailure(string Code, string Message, string Category);

    private async Task<(IActionResult? Replay, bool Claimed)> BeginIdempotentRequestAsync(
        string? userId, string routeKey, string requestHash)
    {
        var key = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(userId) || _idempotency is null)
            return (null, false);
        if (key.Length > 128)
            return (BadRequest(new { success = false, error = new { code = "invalid_idempotency_key", message = "Idempotency-Key must be 128 characters or fewer." } }), false);

        var claim = await _idempotency.TryBeginAsync(key, userId, routeKey, requestHash, ct: HttpContext.RequestAborted);
        if (claim.Outcome == IdempotencyClaimOutcome.Claimed)
            return (null, true);

        if (claim.Outcome == IdempotencyClaimOutcome.Completed)
            return (new ContentResult { StatusCode = claim.StatusCode, ContentType = "application/json", Content = claim.ResponseBody }, false);

        var code = claim.Outcome == IdempotencyClaimOutcome.Mismatch ? "idempotency_key_reused" : "idempotency_in_progress";
        var message = claim.Outcome == IdempotencyClaimOutcome.Mismatch
            ? "This Idempotency-Key was already used with a different upload payload."
            : "An upload with this Idempotency-Key is still processing.";
        return (StatusCode(StatusCodes.Status409Conflict, new { success = false, error = new { code, message } }), false);
    }

    private Task SaveIdempotentResponseAsync(string? userId, string routeKey, int statusCode, object body)
    {
        var key = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(userId) || _idempotency is null)
            return Task.CompletedTask;
        return _idempotency.CompleteAsync(key, userId, routeKey, statusCode,
            JsonSerializer.Serialize(body, IdempotencyJson), ct: HttpContext.RequestAborted);
    }

    private Task MarkIdempotentRequestFailedAsync(string? userId, string routeKey)
    {
        var key = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(userId) || _idempotency is null)
            return Task.CompletedTask;
        return _idempotency.MarkFailedAsync(key, userId, routeKey, HttpContext.RequestAborted);
    }

    private static string ComputeRequestHash(IEnumerable<UploadTrackRequest> requests)
    {
        var stable = requests.Select(x => new
        {
            x.Title, x.Description, x.Genre, x.PrimaryGenre, x.Subgenre, x.Tempo, x.Mood,
            x.Price, x.LicenseType, x.Tags, x.NonExclusivePrice, x.ExclusivePrice,
            x.CopyrightBuyoutPrice, x.NonExclusivePriceCents, x.ExclusivePriceCents,
            x.CopyrightBuyoutPriceCents, x.AlbumAssignmentType, x.CollectionId,
            x.NewAlbumTitle, x.NewAlbumDescription, x.SaveAsDraft,
            audio = x.Audio is null ? null : new { x.Audio.FileName, x.Audio.Length, x.Audio.ContentType },
            cover = x.CoverArt is null ? null : new { x.CoverArt.FileName, x.CoverArt.Length, x.CoverArt.ContentType },
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stable, IdempotencyJson))));
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
