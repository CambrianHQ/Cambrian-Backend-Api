using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("")]
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

    [Authorize]
    [RequireCreatorTier]
    [HttpPost("upload")]
    [RequestSizeLimit(150 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] UploadTrackRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("EVENT: UploadStarted userId:{UserId} title:{Title}", userId, request.Title);
        request.CreatorId = userId;

        try
        {
            var result = await _upload.Upload(request);
            _logger.LogInformation("EVENT: UploadCompleted userId:{UserId} trackId:{TrackId} title:{Title}", userId, result, request.Title);

            // Invalidate catalog cache so the new track appears immediately
            if (_cache is MemoryCache mc)
                mc.Compact(1.0);

            return CreatedResponse(result, "Track uploaded successfully.");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "EVENT: UploadRejected userId:{UserId} title:{Title}", userId, request.Title);
            return ErrorResponse(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "EVENT: UploadBlocked userId:{UserId} title:{Title}", userId, request.Title);
            return ErrorResponse(ex.Message);
        }
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