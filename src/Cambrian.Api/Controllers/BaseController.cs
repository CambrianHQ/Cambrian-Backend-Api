using Cambrian.Api.Common;
using Microsoft.AspNetCore.Mvc;

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
}
