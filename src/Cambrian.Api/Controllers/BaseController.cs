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
}
