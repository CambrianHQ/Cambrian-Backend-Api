using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Application.AI.Discovery.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("ai-discovery")]
[AllowAnonymous]
public class AiDiscoveryController : ControllerBase
{
    private readonly ITrackDiscoveryService _discovery;

    public AiDiscoveryController(ITrackDiscoveryService discovery)
    {
        _discovery = discovery;
    }

    [HttpGet("tracks/search")]
    public async Task<IActionResult> Search([FromQuery] SearchTracksQuery query)
    {
        if (query.PageSize is < 1 or > 100) query.PageSize = 10;
        if (query.Page < 1) query.Page = 1;

        var result = await _discovery.SearchAsync(query);
        return Ok(result);
    }

    [HttpGet("tracks/{trackId}")]
    public async Task<IActionResult> GetTrackDetails(string trackId)
    {
        var result = await _discovery.GetTrackDetailsAsync(trackId);
        if (result is null) return NotFound(new { error = "Track not found." });
        return Ok(new { track = result });
    }

    [HttpGet("tracks/{trackId}/licenses")]
    public async Task<IActionResult> GetLicenses(string trackId)
    {
        var result = await _discovery.GetLicenseOptionsAsync(trackId);
        if (result is null) return NotFound(new { error = "Track not found." });
        return Ok(new { licenses = result });
    }

    [HttpGet("tracks/{trackId}/preview")]
    public async Task<IActionResult> GetPreview(string trackId)
    {
        var result = await _discovery.GetPreviewAsync(trackId);
        if (result is null) return NotFound(new { error = "Track not found." });
        return Ok(new { preview = result });
    }

    [HttpGet("creators/{creatorId}")]
    public async Task<IActionResult> GetCreatorProfile(string creatorId)
    {
        var result = await _discovery.GetCreatorProfileAsync(creatorId);
        if (result is null) return NotFound(new { error = "Creator not found." });
        return Ok(new { creator = result });
    }
}
