using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Action filter that verifies the authenticated user has Tier == "creator".
/// Reads the "tier" claim from the JWT first (no DB call). Falls back to
/// UserManager only for legacy tokens that lack the claim.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireCreatorTierAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Fast path: read tier from JWT claim (set by AuthService.GenerateJwt)
        var tier = context.HttpContext.User.FindFirstValue("tier");

        // Slow path: fall back to DB for legacy tokens missing the claim
        if (string.IsNullOrEmpty(tier))
        {
            var userManager = context.HttpContext.RequestServices
                .GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
            {
                context.Result = new UnauthorizedResult();
                return;
            }
            tier = (user.Tier ?? "free").ToLowerInvariant();
        }

        if (tier != "creator" && tier != "pro")
        {
            context.Result = new ObjectResult(ApiResponse.Fail("Creator tier required."))
            {
                StatusCode = 403
            };
            return;
        }

        await next();
    }
}
