using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("health")]
public class HealthController : ControllerBase
{
    private readonly IHealthService _health;
    private readonly IObjectStorage _storage;

    public HealthController(IHealthService health, IObjectStorage storage)
    {
        _health = health;
        _storage = storage;
    }

    [HttpGet]
    public IActionResult Get()
    {
        // Public liveness only. Never expose environment, row counts, storage,
        // Stripe, or database details on the unauthenticated health route.
        return Ok(new { status = "ok" });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("details")]
    public async Task<IActionResult> Details()
    {
        var result = await _health.GetHealthAsync();
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

    /// <summary>
    /// Admin-only diagnostic probe: runs HeadBucket and (optionally) HeadObject
    /// for the supplied key against the storage provider this backend is
    /// currently using. Returns structured JSON so we can see the exact failure
    /// layer (credentials, endpoint, bucket, object) without parsing logs.
    /// Example: GET /health/storage-probe?key=covers/abc/def.jpg
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpGet("storage-probe")]
    public async Task<IActionResult> StorageProbe([FromQuery] string? key = null)
    {
        var result = await _storage.ProbeAsync(key);
        return Ok(result);
    }
}
