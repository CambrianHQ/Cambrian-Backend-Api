using System.Security.Claims;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Action filter that verifies the authenticated user has Tier == "creator".
/// Use on controllers/actions that require creator-tier access.
/// This is separate from role-based auth because Tier is a subscription-level
/// concept, not an identity role.
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

        var userManager = context.HttpContext.RequestServices
            .GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var tier = (user.Tier ?? "free").ToLowerInvariant();
        if (tier != "creator")
        {
            context.Result = new ObjectResult(new { success = false, error = "Creator tier required." })
            {
                StatusCode = 403
            };
            return;
        }

        await next();
    }
}
