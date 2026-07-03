using Cambrian.Application.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>Public, cacheable runtime configuration consumed by frontend clients.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
    [HttpGet("entitlements")]
    [ResponseCache(Duration = PlanEntitlements.PublicCacheTtlSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(PublicPlanEntitlementsResponse), StatusCodes.Status200OK)]
    public IActionResult Entitlements()
    {
        Response.Headers.CacheControl = $"public, max-age={PlanEntitlements.PublicCacheTtlSeconds}";
        return Ok(PlanEntitlements.PublicContract);
    }
}
