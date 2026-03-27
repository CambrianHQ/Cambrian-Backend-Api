using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Development-only middleware that injects a synthetic ClaimsPrincipal
/// when the request carries <c>Bearer test-audit-token</c>.
/// Placed BEFORE UseAuthentication() — sets the principal and strips the
/// fake token so the JWT handler sees no bearer and returns NoResult,
/// leaving context.User intact.
/// Completely inert outside IsDevelopment().
/// </summary>
public class DevAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;

    public DevAuthMiddleware(RequestDelegate next, IWebHostEnvironment env)
    {
        _next = next;
        _env = env;
    }

    public async Task Invoke(HttpContext context)
    {
        if (_env.IsDevelopment())
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();

            if (authHeader == "Bearer test-audit-token")
            {
                // Use the seeded admin account so DB lookups succeed
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, "c8c2af80-1de2-4c71-bc93-a237848adad7"),
                    new(ClaimTypes.Email, "admin@cambrian.local"),
                    new(ClaimTypes.Role, "Admin"),
                };

                var identity = new ClaimsIdentity(claims, "DevAuth");
                var principal = new ClaimsPrincipal(identity);
                context.User = principal;

                // Strip the fake token so the JWT handler finds no bearer
                // and returns NoResult — leaving context.User untouched.
                context.Request.Headers.Remove("Authorization");

                // Also set a successful auth result for any code that calls AuthenticateAsync()
                var ticket = new AuthenticationTicket(principal, "DevAuth");
                context.Features.Set<IAuthenticateResultFeature>(
                    new DevAuthResultFeature(AuthenticateResult.Success(ticket)));
            }
        }

        await _next(context);
    }

    private sealed class DevAuthResultFeature : IAuthenticateResultFeature
    {
        public DevAuthResultFeature(AuthenticateResult result) => AuthenticateResult = result;
        public AuthenticateResult? AuthenticateResult { get; set; }
    }
}
