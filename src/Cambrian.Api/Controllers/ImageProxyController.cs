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
        "covers", "avatars", "banners", "creator-profiles", "creator-covers", "images"
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

    // Hidden from Swagger / OpenAPI: this is a server-side catch-all proxy
    // (path uses "{**key}" syntax) that Swashbuckle and openapi generators
    // do not represent cleanly. The contract validators have explicit
    // [ApiExplorerSettings(IgnoreApi = true)] support so this stays out of
    // both the architecture-compliance check and the breaking-change diff.
    [ApiExplorerSettings(IgnoreApi = true)]
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

        // ETag based on the key — same key always serves the same content.
        // Allows browsers to skip re-download on subsequent renders.
        var etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16]}\"";
        if (Request.Headers.IfNoneMatch.ToString() == etag)
            return StatusCode(304);

        var file = await _storage.OpenReadAsync(key);
        if (file is null)
            return NotFoundResponse("Image not found.");

        // Set Content-Length so browsers can render progressively and cache correctly.
        // S3 response streams are non-seekable, so FileStreamResult won't infer the length.
        if (file.Length > 0)
            Response.ContentLength = file.Length;

        Response.Headers.ETag = etag;
        return File(file.Stream, file.ContentType);
    }
}