using System.Diagnostics;
using System.Security.Claims;

namespace Cambrian.Api.Middleware;

public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Attach a Request ID for correlation
        var requestId = context.Request.Headers["X-Request-ID"].FirstOrDefault()
                        ?? Guid.NewGuid().ToString("N")[..12];
        context.Items["RequestId"] = requestId;
        context.Response.Headers["X-Request-ID"] = requestId;

        var sw = Stopwatch.StartNew();
        await _next(context);
        sw.Stop();

        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anon";
        _logger.LogInformation(
            "HTTP {Method} {Path} → {StatusCode} in {ElapsedMs}ms [rid:{RequestId} uid:{UserId}]",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            sw.ElapsedMilliseconds,
            requestId,
            userId);
    }
}
