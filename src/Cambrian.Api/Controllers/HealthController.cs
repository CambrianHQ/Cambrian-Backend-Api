using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
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
    [Authorize(Roles = "Admin")]
    [HttpGet("storage")]
    public async Task<IActionResult> StorageDiag()
    {
        var result = await _health.GetStorageDiagAsync();
        return Ok(result);
    }

    /// <summary>
    /// Admin-only: audit ALL tracks for missing audio files in R2/S3.
    /// Returns track IDs and keys where the file cannot be found in storage.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpGet("audio-audit")]
    public async Task<IActionResult> AudioAudit()
    {
        var result = await _health.AuditAudioKeysAsync();
        return Ok(result);
    }
}
