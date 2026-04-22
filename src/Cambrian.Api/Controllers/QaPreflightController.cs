using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

/// <summary>
/// Hard deployment-gate endpoint. Unlike <c>/health</c> which always returns 200
/// so Render keeps routing traffic, <c>/qa-preflight</c> returns HTTP 503 when
/// any critical dependency is broken. CI post-deploy smoke scripts check this
/// endpoint and fail the release if it does not return 200.
/// </summary>
[Route("qa-preflight")]
public sealed class QaPreflightController : ControllerBase
{
    private readonly IPreflightService _preflight;

    public QaPreflightController(IPreflightService preflight)
    {
        _preflight = preflight;
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var result = await _preflight.RunAsync(ct);
        if (result.Degraded)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, result.Body);
        return Ok(result.Body);
    }
}
