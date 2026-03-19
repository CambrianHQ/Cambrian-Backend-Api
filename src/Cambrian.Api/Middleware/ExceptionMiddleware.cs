using System.Net;
using System.Text.Json;
using Cambrian.Api.Common;

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
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
            {
                _logger.LogWarning("Response already started — cannot write error response for {Method} {Path}",
                    context.Request.Method, context.Request.Path);
                return;
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = ex switch
            {
                UnauthorizedAccessException => (int)HttpStatusCode.Unauthorized,
                KeyNotFoundException        => (int)HttpStatusCode.NotFound,
                ArgumentException           => (int)HttpStatusCode.BadRequest,
                InvalidOperationException   => (int)HttpStatusCode.BadRequest,
                _                           => (int)HttpStatusCode.InternalServerError
            };

            var message = context.Response.StatusCode == 500 && _isProduction
                ? "An unexpected error occurred."
                : ex.Message;

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(ApiResponse.Fail(message), _json));
        }
    }
}
