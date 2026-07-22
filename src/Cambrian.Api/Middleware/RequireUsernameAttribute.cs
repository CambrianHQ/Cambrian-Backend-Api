using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Application.Common;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Action filter that enforces username completion before accessing protected routes.
/// A user whose UserName is null, empty, or still equals their email address has not
/// completed onboarding — they must call POST /auth/set-username first.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireUsernameAttribute : Attribute, IAsyncActionFilter
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
        var user = await userManager.FindByIdAsync(userId);

        if (user is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Admins managing creator resources (e.g. payouts) don't need a username
        if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (!UsernameHelper.IsSet(user))
        {
            // Fallback: the Creators table is the canonical source of truth for username
            // completion. Users who registered via Google OAuth or before the Creator table
            // migration may have UserName == Email in Identity but a valid Creator row.
            var creatorRepo = context.HttpContext.RequestServices
                .GetService<Cambrian.Application.Interfaces.ICreatorIdentityRepository>();
            if (creatorRepo is not null)
            {
                var creatorId = await creatorRepo.GetCreatorIdForUserAsync(userId);
                if (creatorId.HasValue)
                {
                    await next();
                    return;
                }
            }

            context.Result = new ObjectResult(
                new
                {
                    success = false,
                    error = "Username required. Please complete your profile setup at POST /auth/set-username.",
                    code = "USERNAME_REQUIRED"
                })
            {
                StatusCode = 403
            };
            return;
        }

        await next();
    }
}
