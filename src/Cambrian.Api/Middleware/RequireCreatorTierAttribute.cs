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
/// Reads the role claim from the JWT first, but reconciles against the current database
/// value so recently-promoted creators are not blocked by stale session tokens.
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

        var userManager = context.HttpContext.RequestServices
            .GetRequiredService<UserManager<ApplicationUser>>();

        // Fast path: trust an explicit Creator/Admin claim.
        var claimedRole = context.HttpContext.User.FindFirstValue(ClaimTypes.Role);
        if (string.Equals(claimedRole, "Creator", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(claimedRole, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // Reconcile with the database so users promoted after token issuance
        // can immediately enter creator flows without a forced re-login.
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var role = user.Role;

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
