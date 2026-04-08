using System.Security.Claims;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.Catalog;
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

    [Authorize(Policy = "VerifiedEmail")]
    [RequireCreatorTier]
    [RequireUsername]
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

        // SECURITY: Validate image magic bytes to prevent disguised file uploads
        var imgMagic = new Dictionary<string, byte[][]>
        {
            [".jpg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
            [".jpeg"] = [new byte[] { 0xFF, 0xD8, 0xFF }],
            [".png"] = [new byte[] { 0x89, 0x50, 0x4E, 0x47 }],
            [".webp"] = [System.Text.Encoding.ASCII.GetBytes("RIFF")]
        };
        if (imgMagic.TryGetValue(ext, out var signatures))
        {
            var headerBuf = new byte[12];
            var bytesRead = await stream.ReadAsync(headerBuf);
            stream.Position = 0;
            var matched = false;
            foreach (var sig in signatures)
            {
                if (bytesRead >= sig.Length && headerBuf.AsSpan(0, sig.Length).SequenceEqual(sig))
                {
                    matched = true;
                    // WebP: also verify "WEBP" at offset 8
                    if (ext == ".webp" && (bytesRead < 12 || !headerBuf.AsSpan(8, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("WEBP"))))
                        matched = false;
                    break;
                }
            }
            if (!matched)
                return ErrorResponse("File content does not match expected image format. The file may be corrupted or disguised.");
        }

        await _storage.UploadAsync(stream, key, contentType);
        var url = _storage.GenerateSignedUrl(key);

        return OkResponse(new { url });
    }
}