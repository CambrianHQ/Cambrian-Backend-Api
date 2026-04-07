using Cambrian.Api.Common;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Base controller providing typed response helpers and uniform formatting.
/// </summary>
[ApiController]
public class BaseController : ControllerBase
{
    /// <summary>200 OK with data envelope.</summary>
    protected IActionResult OkResponse<T>(T data, string? message = null) =>
        Ok(ApiResponse<T>.Ok(data, message));

    /// <summary>201 Created with data envelope.</summary>
    protected IActionResult CreatedResponse<T>(T data, string? message = null) =>
        StatusCode(201, ApiResponse<T>.Ok(data, message));

    /// <summary>200 OK with message-only envelope (void operations).</summary>
    protected IActionResult MessageResponse(string message) =>
        Ok(ApiResponse.Ok(message));

    /// <summary>400 Bad Request with error envelope.</summary>
    protected IActionResult ErrorResponse(string error) =>
        BadRequest(ApiResponse.Fail(error));

    /// <summary>404 Not Found with error envelope.</summary>
    protected IActionResult NotFoundResponse(string error = "Resource not found.") =>
        NotFound(ApiResponse.Fail(error));

    /// <summary>403 Forbidden with error envelope.</summary>
    protected IActionResult ForbiddenResponse(string error = "Access denied.") =>
        StatusCode(403, ApiResponse.Fail(error));

    /// <summary>
    /// Returns the authenticated user's ID from the JWT NameIdentifier claim.
    /// All callers must be on [Authorize] endpoints; returns null only in pathological cases
    /// where the middleware has been misconfigured.
    /// </summary>
    protected string? GetRequiredUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    /// <summary>409 Conflict with error envelope.</summary>
    protected IActionResult ConflictResponse(string error = "Resource already exists.") =>
        StatusCode(409, ApiResponse.Fail(error));

    /// <summary>
    /// Convert a relative URL (e.g. /uploads/key) to an absolute URL so the
    /// frontend on a different origin can fetch the file correctly.
    /// Already-absolute URLs (S3/R2 pre-signed) are returned unchanged.
    /// </summary>
    protected string ResolveAbsoluteUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return url ?? "";
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            return url;
        // Request may be null in unit tests — return relative path as-is
        if (Request is null)
            return url;
        // Ensure a "/" separates the host from the path so we never produce
        // URLs like "onrender.comtracks/..." when the stored key has no leading slash.
        var separator = url.StartsWith('/') ? "" : "/";
        return $"{Request.Scheme}://{Request.Host}{separator}{url}";
    }

    /// <summary>
    /// Resolve an image URL (stored as an object key, absolute R2/S3 URL, or local /uploads/ path)
    /// to a URL proxied through the backend's /images/ endpoint. This avoids CORS issues
    /// with direct R2/S3 bucket access.
    /// </summary>
    protected string ResolveImageUrl(string? rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl))
            return rawUrl ?? "";
        // Local storage paths (/uploads/...) — resolve to absolute backend URL
        if (rawUrl.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return ResolveAbsoluteUrl(rawUrl);
        // Absolute R2/S3 URL — extract the object key and proxy through /images/
        if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            {
                var key = uri.AbsolutePath.TrimStart('/');
                // Strip bucket-name prefix. R2/S3 URLs have the path /{bucket}/{key}.
                // If the first segment isn't a known image prefix, it's the bucket name.
                key = StripBucketPrefix(key);
                if (!string.IsNullOrEmpty(key))
                    return ResolveAbsoluteUrl($"/images/{key}");
            }
            return rawUrl; // unrecognized — pass through
        }
        // Bare object key (e.g. covers/abc/img.jpg) — proxy through /images/
        // Also strip bucket prefix in case DB stored "cambrianaudio/covers/..."
        return ResolveAbsoluteUrl($"/images/{StripBucketPrefix(rawUrl)}");
    }

    // Known first-segment prefixes for image object keys.
    private static readonly HashSet<string> KnownImagePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "covers", "avatars", "banners", "creator-profiles"
    };

    /// <summary>
    /// If the first path segment is not a known image prefix (covers, avatars, etc.)
    /// it is the S3/R2 bucket name embedded in the URL path — strip it.
    /// e.g. "cambrianaudio/covers/abc.jpg" → "covers/abc.jpg"
    /// </summary>
    private static string StripBucketPrefix(string key)
    {
        var slash = key.IndexOf('/');
        if (slash > 0)
        {
            var first = key[..slash];
            if (!KnownImagePrefixes.Contains(first))
                return key[(slash + 1)..];
        }
        return key;
    }
}
