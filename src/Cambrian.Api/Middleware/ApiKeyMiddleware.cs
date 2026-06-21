using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Api.Security;
using Cambrian.Application.Interfaces;

namespace Cambrian.Api.Middleware;

/// <summary>
/// Authenticates requests that carry an X-API-Key header.
/// Runs AFTER UseAuthentication and only activates for endpoints carrying
/// <see cref="AllowApiKeyAttribute"/> metadata. It never turns an API key into
/// generic account authentication.
/// </summary>
public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopeFactory;

    public ApiKeyMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory)
    {
        _next = next;
        _scopeFactory = scopeFactory;
    }

    public async Task InvokeAsync(HttpContext context, IApiKeyRepository apiKeyRepo)
    {
        // Already authenticated by JWT or cookie — nothing to do.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-API-Key", out var rawKeyValues))
        {
            await _next(context);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<AllowApiKeyAttribute>() is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                error = "API keys are not accepted on this route."
            });
            return;
        }

        var rawKey = rawKeyValues.ToString();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid API key." });
            return;
        }

        var keyHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))
        ).ToLower();

        var apiKey = await apiKeyRepo.GetByHashAsync(keyHash);

        if (apiKey is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid or revoked API key." });
            return;
        }

        var keyId = apiKey.Id;
        context.Response.OnCompleted(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
                await repo.UpdateLastUsedAsync(keyId);
            }
            catch
            {
                // Non-critical — do not propagate.
            }
        });

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.UserId),
            new Claim(AuthenticationConstants.AuthMethodClaim, AuthenticationConstants.AuthMethodApiKey),
            new Claim("api_key_prefix", apiKey.KeyPrefix),
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));

        await _next(context);
    }
}
