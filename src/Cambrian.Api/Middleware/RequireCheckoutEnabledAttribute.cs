using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Cambrian.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Operational kill switch for every endpoint that can create a new charge.
/// Read-only billing, support, history, and webhook endpoints remain available.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireCheckoutEnabledAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (CheckoutAvailability.IsEnabled(configuration))
        {
            await next();
            return;
        }

        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILogger<RequireCheckoutEnabledAttribute>>();
        var userId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? context.HttpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                     ?? "anonymous";

        logger.LogWarning(
            "Blocked charge creation because checkout is disabled. userId={UserId} method={Method} path={Path}",
            userId,
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path);

        context.Result = new ObjectResult(CheckoutAvailability.BuildBlockedResponse())
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
    }
}
