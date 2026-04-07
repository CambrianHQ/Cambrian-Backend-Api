using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Proxies image requests through the backend so that R2/S3 objects are served
/// with proper CORS headers. This eliminates the need for bucket-level CORS
/// configuration on Cloudflare R2.
///
/// Paths:
///   GET /images/covers/{creatorId}/{file}
///   GET /images/avatars/{file}
///   GET /images/banners/{file}
///   GET /images/creator-profiles/{file}
/// </summary>
[Route("images")]
[AllowAnonymous]
public class ImageProxyController : BaseController
{
    private readonly IObjectStorage _storage;
    private readonly IMemoryCache _cache;

    // Only allow image-like object keys — block path traversal and non-image paths.
    private static readonly HashSet<string> AllowedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "covers", "avatars", "banners", "creator-profiles"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".avif"
    };

    public ImageProxyController(IObjectStorage storage, IMemoryCache cache)
    {
        _storage = storage;
        _cache = cache;
    }

    [HttpGet("{**key}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> GetImage(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return NotFoundResponse("No image key provided.");

        // Security: block path traversal
        if (key.Contains("..") || key.Contains('\\'))
            return ErrorResponse("Invalid image key.");

        // Security: only serve from allowed prefixes
        var firstSegment = key.Split('/')[0];
        if (!AllowedPrefixes.Contains(firstSegment))
            return NotFoundResponse("Image not found.");

        // Security: only serve image file extensions
        var ext = Path.GetExtension(key);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return NotFoundResponse("Image not found.");

        var file = await _storage.OpenReadAsync(key);
        if (file is null)
            return NotFoundResponse("Image not found.");

        // Set Content-Length so browsers can render progressively and cache correctly.
        // S3 response streams are non-seekable, so FileStreamResult won't infer the length.
        if (file.Length > 0)
            Response.ContentLength = file.Length;

        return File(file.Stream, file.ContentType);
    }
}