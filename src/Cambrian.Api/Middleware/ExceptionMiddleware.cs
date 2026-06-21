using System.Net;
using System.Text.Json;
using Cambrian.Api.Common;
using Cambrian.Application.Exceptions;

namespace Cambrian.Api.Middleware;

public sealed class ExceptionMiddleware
{
    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;
    private readonly bool _isProduction;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _isProduction = env.IsProduction();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var logMessage = _isProduction
                ? $"{ex.GetType().Name}: {(ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message)}"
                : ex.ToString();
            _logger.LogError("Unhandled exception for {Method} {Path}: {Error}",
                context.Request.Method, context.Request.Path, logMessage);

            if (context.Response.HasStarted)
            {
                _logger.LogWarning("Response already started — cannot write error response for {Method} {Path}",
                    context.Request.Method, context.Request.Path);
                return;
            }

            context.Response.ContentType = "application/json";

            // Plan-gated actions (e.g. Free track limit) surface a 402 with a stable
            // machine-readable code the frontend uses to launch the upgrade flow.
            if (ex is UpgradeRequiredException upgrade)
            {
                context.Response.StatusCode = (int)HttpStatusCode.PaymentRequired;
                await context.Response.WriteAsync(JsonSerializer.Serialize(
                    new { success = false, error = upgrade.Message, code = upgrade.Code }, _json));
                return;
            }

            context.Response.StatusCode = ex switch
            {
                PayoutPendingException       => (int)HttpStatusCode.ServiceUnavailable,
                ForbiddenException          => (int)HttpStatusCode.Forbidden,
                UnauthorizedAccessException => (int)HttpStatusCode.Unauthorized,
                KeyNotFoundException        => (int)HttpStatusCode.NotFound,
                ArgumentException           => (int)HttpStatusCode.BadRequest,
                InvalidOperationException   => (int)HttpStatusCode.BadRequest,
                _                           => (int)HttpStatusCode.InternalServerError
            };

            // ArgumentException and InvalidOperationException carry user-facing validation
            // messages (e.g. "Audio file is required.", "MIME type not allowed") that are
            // safe to surface in production. Only internal 5xx errors use generic text.
            var message = _isProduction
                && ex is not ArgumentException
                && ex is not InvalidOperationException
                && ex is not PayoutPendingException
                ? context.Response.StatusCode switch
                {
                    500 => "An unexpected error occurred.",
                    401 => "Authentication is required.",
                    403 => "Access denied.",
                    404 => "The requested resource was not found.",
                    _   => "An error occurred."
                }
                : ex.Message;

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(ApiResponse.Fail(message), _json));
        }
    }
}
