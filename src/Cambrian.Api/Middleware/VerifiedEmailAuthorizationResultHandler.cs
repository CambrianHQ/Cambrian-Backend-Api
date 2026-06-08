using System.Text.Json;
using Microsoft.AspNetCore.Authorization;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Returns a structured 403 body when an authenticated user fails the VerifiedEmail policy.
/// </summary>
public sealed class VerifiedEmailForbiddenResponseMiddleware
{
    private readonly RequestDelegate _next;

    public VerifiedEmailForbiddenResponseMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        if (context.Response.HasStarted
            || context.Response.StatusCode != StatusCodes.Status403Forbidden
            || context.User.Identity?.IsAuthenticated != true
            || !EndpointRequiresVerifiedEmail(context)
            || UserEmailIsVerified(context)
            || context.Response.ContentLength is > 0)
        {
            // Only an authenticated-but-unverified user hitting a VerifiedEmail-gated
            // endpoint gets the structured body. A 403 for a verified user came from some
            // other policy (e.g. a capability or role gate) and must not be mislabeled.
            return;
        }

        context.Response.ContentType = "application/json";
        context.Response.ContentLength = null;

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "email_not_verified",
                message = "Verify your email before accessing this endpoint."
            }
        }));
    }

    private static bool EndpointRequiresVerifiedEmail(HttpContext context)
    {
        var authorizeData = context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>();
        return authorizeData?.Any(a =>
            string.Equals(a.Policy, "VerifiedEmail", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool UserEmailIsVerified(HttpContext context) =>
        context.User.HasClaim(c => c.Type == "email_verified" && c.Value == "true");
}
