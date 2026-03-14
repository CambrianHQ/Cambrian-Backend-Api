using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("health")]
public class HealthController : ControllerBase
{
    private readonly IHealthService _health;

    public HealthController(IHealthService health)
    {
        _health = health;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var result = await _health.GetHealthAsync();
        // Always return 200 so Render health-check passes and routes traffic
        return Ok(result);
    }

    /// <summary>
    /// Diagnostic endpoint to check audio storage state for a sample track.
    /// Returns the raw AudioUrl and whether the file exists in storage.
    /// </summary>
    [HttpGet("storage")]
    public async Task<IActionResult> StorageDiag()
    {
        var result = await _health.GetStorageDiagAsync();
        return Ok(result);
    }
}
