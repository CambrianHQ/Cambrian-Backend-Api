using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Records an analytics event for every public-API call (one row per request).
/// Fire-and-forget: never blocks the response and never throws into the
/// caller's stack — analytics failures must not impact API behavior.
///
/// Apply at controller level on the v1 controllers via
/// <c>[ServiceFilter(typeof(ApiUsageActionFilter))]</c>.
/// </summary>
public sealed class ApiUsageActionFilter : IAsyncActionFilter
{
    private const string EventType = "api_call";

    private readonly IAnalyticsService _analytics;
    private readonly ILogger<ApiUsageActionFilter> _logger;

    public ApiUsageActionFilter(IAnalyticsService analytics, ILogger<ApiUsageActionFilter> logger)
    {
        _analytics = analytics;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var sw = Stopwatch.StartNew();
        var executed = await next();
        sw.Stop();

        // Best-effort metadata. The route is the canonical identifier; the user/key
        // is what makes a usage row useful for billing/analytics.
        var http = executed.HttpContext;
        var route = http.Request.Method + " " + (http.Request.Path.Value ?? "");
        var status = http.Response.StatusCode;
        var elapsedMs = (int)sw.ElapsedMilliseconds;

        // The middleware sets ClaimTypes.AuthenticationMethod = "api_key" when
        // the call was authed by an X-API-Key header (vs JWT bearer).
        var authMethod = http.User.FindFirstValue("auth_method") ?? "jwt_or_anonymous";
        var keyPrefix = http.User.FindFirstValue("api_key_prefix");

        var metadata = JsonSerializer.Serialize(new
        {
            route,
            status,
            elapsedMs,
            authMethod,
            keyPrefix,
        });

        try
        {
            await _analytics.RecordEventAsync(
                new AnalyticsEventRequest { Type = EventType, MetadataJson = metadata },
                http.User,
                http.RequestAborted);
        }
        catch (Exception ex)
        {
            // Never propagate — analytics is observational, not load-bearing.
            _logger.LogWarning(ex, "API usage analytics event failed (route={Route} status={Status})", route, status);
        }
    }
}
