using System.Security.Claims;
using Cambrian.Application.Auth;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Middleware that resolves capabilities for the authenticated user and stores them
/// in HttpContext.Items["Capabilities"] for downstream policy evaluation.
/// Runs after authentication middleware — skips unauthenticated requests.
/// </summary>
public sealed class CapabilityMiddleware
{
    private readonly RequestDelegate _next;

    public CapabilityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            await _next(context);
            return;
        }

        var userManager = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var resolver = context.RequestServices.GetRequiredService<ICapabilityResolver>();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            await _next(context);
            return;
        }

        var capabilities = await resolver.ResolveAsync(user);
        context.Items["Capabilities"] = capabilities;

        await _next(context);
    }
}
