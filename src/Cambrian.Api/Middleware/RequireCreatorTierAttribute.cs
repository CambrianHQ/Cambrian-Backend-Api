using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.IdentityModel.Tokens.Jwt;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Action filter that verifies the authenticated user has Role == "Creator" (or "Admin").
/// Reads the role claim from the JWT first (no DB call). Falls back to
/// UserManager only for legacy tokens that lack the claim.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireCreatorTierAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? context.HttpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (userId is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Fast path: read role from JWT claim
        var role = context.HttpContext.User.FindFirstValue(ClaimTypes.Role);

        // Fall back to DB for legacy tokens missing the role claim
        if (string.IsNullOrEmpty(role))
        {
            var userManager = context.HttpContext.RequestServices
                .GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
            {
                context.Result = new UnauthorizedResult();
                return;
            }
            role = user.Role;
        }

        if (!string.Equals(role, "Creator", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new ObjectResult(ApiResponse.Fail("Creator account required to upload tracks."))
            {
                StatusCode = 403
            };
            return;
        }

        await next();
    }
}
