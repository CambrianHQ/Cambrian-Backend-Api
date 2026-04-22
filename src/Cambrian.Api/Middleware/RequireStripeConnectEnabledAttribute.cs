using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Blocks payout entry points when Stripe Connect is not enabled system-wide.
/// This lets operations pause all payout flows without changing route auth.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireStripeConnectEnabledAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var flags = context.HttpContext.RequestServices.GetRequiredService<IFeatureFlagRepository>();
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILogger<RequireStripeConnectEnabledAttribute>>();

        var enabled = await flags.IsEnabledAsync(StripeConnectAvailability.FeatureFlagName);
        if (enabled)
        {
            await next();
            return;
        }

        var userId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? context.HttpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                     ?? "anonymous";
        var role = context.HttpContext.User.FindFirstValue(ClaimTypes.Role) ?? "unknown";

        logger.LogWarning(
            "Blocked payout request because {FeatureFlag} is disabled. userId={UserId} role={Role} method={Method} path={Path}",
            StripeConnectAvailability.FeatureFlagName,
            userId,
            role,
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path);

        context.Result = new ObjectResult(StripeConnectAvailability.BuildBlockedResponse())
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
    }
}
