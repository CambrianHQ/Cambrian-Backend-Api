using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

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
    private const string ImmutableImageCacheControl = "public, max-age=31536000, immutable";

    private readonly IObjectStorage _storage;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ImageProxyController> _logger;

    // Only allow image-like object keys — block path traversal and non-image paths.
    private static readonly HashSet<string> AllowedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "covers", "avatars", "banners", "creator-profiles", "creator-covers", "images"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".avif"
    };

    private static readonly HashSet<string> AllowedCorsOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "https://cdn.cambrianmusic.com",
        "https://cambrianmusic.com",
        "https://www.cambrianmusic.com"
    };

    public ImageProxyController(IObjectStorage storage, IMemoryCache cache, ILogger<ImageProxyController> logger)
    {
        _storage = storage;
        _cache = cache;
        _logger = logger;
    }

    // Internal infrastructure endpoint — CDN-style image proxy, not part of the
    // public API contract. Hidden from Swagger/OpenAPI so the contract validator
    // and breaking-change detector both ignore the catch-all route template
    // (OpenAPI cannot cleanly represent ASP.NET Core's {**key} catch-all).
    //
    // CACHE RULE: only a 2xx with real image bytes may carry the immutable
    // year-long Cache-Control. Stamping it on 404/error responses pinned a
    // transient failure (mid-upload race, cold start, storage blip) in every
    // viewer's browser for a year — and because the key-derived ETag rode
    // along, conditional revalidation answered 304 and re-blessed the broken
    // entry forever (regression: creator avatars "not rendering" for some
    // browsers while fine on fresh clients).
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("{**key}")]
    public async Task<IActionResult> GetImage(string key)
    {
        ApplyCorsHeaders();
        // Failures must never be cached — a retry has to reach the server.
        Response.Headers.CacheControl = "no-store";

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

        StorageFile? file;
        try
        {
            file = await _storage.OpenReadAsync(key);
        }
        catch (Exception ex)
        {
            // Defensive: S3ObjectStorage already swallows exceptions and returns null,
            // but if anything else throws (e.g. a future implementation), we must not
            // surface a 500 to the client for an image request. Log and 404.
            _logger.LogError(ex,
                "[STORAGE-DIAG] ImageProxy OpenReadAsync threw: type={ExceptionType} key={Key} message={Message}",
                ex.GetType().FullName, key, ex.Message);
            return NotFoundResponse("Image not found.");
        }

        if (file is null)
            return NotFoundResponse("Image not found.");

        // The object exists — NOW it is safe to promise immutability. ETag is
        // key-derived (same key always serves the same content), which lets
        // browsers revalidate without re-downloading.
        var etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16]}\"";
        ApplyCacheHeaders(etag);
        if (MatchesIfNoneMatch(etag, Request.Headers.IfNoneMatch.ToString()))
        {
            file.Stream.Dispose();
            return StatusCode(304);
        }

        // Set Content-Length so browsers can render progressively and cache correctly.
        // S3 response streams are non-seekable, so FileStreamResult won't infer the length.
        if (file.Length > 0)
            Response.ContentLength = file.Length;

        return File(file.Stream, file.ContentType);
    }

    private void ApplyCorsHeaders()
    {
        var origin = Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin) || !AllowedCorsOrigins.Contains(origin))
            return;

        Response.Headers["Access-Control-Allow-Origin"] = origin;
        Response.Headers["Access-Control-Allow-Methods"] = "GET";
        Response.Headers["Access-Control-Expose-Headers"] = "Cache-Control, ETag, Content-Length, Content-Type";
        Response.Headers.Vary = "Origin";
    }

    private void ApplyCacheHeaders(string etag)
    {
        Response.Headers.CacheControl = ImmutableImageCacheControl;
        Response.Headers.ETag = etag;
    }

    private static bool MatchesIfNoneMatch(string etag, string ifNoneMatch)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
            return false;

        foreach (var candidate in ifNoneMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidate == "*" || string.Equals(candidate, etag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
