using Cambrian.Api.Common;
using Microsoft.AspNetCore.Antiforgery;

namespace Cambrian.Api.Security;

/// <summary>
/// Enforces antiforgery and trusted browser origins only when a state-changing
/// request was authenticated through the auth_token cookie. Bearer-token API
/// clients remain unaffected.
/// </summary>
public sealed class CookieCsrfProtectionMiddleware
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Options,
        HttpMethods.Trace,
    };

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public CookieCsrfProtectionMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (SafeMethods.Contains(context.Request.Method)
            || !context.User.HasClaim(
                AuthenticationConstants.AuthTransportClaim,
                AuthenticationConstants.AuthTransportCookie))
        {
            await _next(context);
            return;
        }

        if (!HasTrustedOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(ApiResponse.Fail("Untrusted request origin."));
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(ApiResponse.Fail("Invalid or missing CSRF token."));
            return;
        }

        await _next(context);
    }

    private bool HasTrustedOrigin(HttpRequest request)
    {
        var candidate = request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidate)
            && Uri.TryCreate(request.Headers.Referer.FirstOrDefault(), UriKind.Absolute, out var referer))
        {
            candidate = referer.GetLeftPart(UriPartial.Authority);
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var origin))
            return false;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddOrigin(allowed, _configuration["App:FrontendUrl"]);

        foreach (var configured in (_configuration["App:CorsOrigins"] ?? "")
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddOrigin(allowed, configured);
        }

        AddOrigin(allowed, $"{request.Scheme}://{request.Host}");
        return allowed.Contains(origin.GetLeftPart(UriPartial.Authority));
    }

    private static void AddOrigin(ISet<string> origins, string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            origins.Add(uri.GetLeftPart(UriPartial.Authority));
    }
}
