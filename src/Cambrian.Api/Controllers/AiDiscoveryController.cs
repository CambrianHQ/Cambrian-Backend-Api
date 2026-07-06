using Cambrian.Application.AI.Discovery.Dtos;
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
    [ProducesResponseType(typeof(AiTrackSearchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] SearchTracksQuery query)
    {
        if (query.PageSize is < 1 or > 100) query.PageSize = 10;
        if (query.Page < 1) query.Page = 1;

        var result = await _discovery.SearchAsync(query);
        return Ok(result);
    }

    [HttpGet("tracks/{trackId}")]
    [ProducesResponseType(typeof(AiTrackDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTrackDetails(string trackId)
    {
        var result = await _discovery.GetTrackDetailsAsync(trackId);
        if (result is null) return NotFound(new { error = "Track not found." });
        return Ok(new AiTrackDetailsResponse { Track = result });
    }

    [HttpGet("tracks/{trackId}/preview")]
    [ProducesResponseType(typeof(AiTrackPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPreview(string trackId)
    {
        var result = await _discovery.GetPreviewAsync(trackId);
        if (result is null) return NotFound(new { error = "Track not found." });
        return Ok(new AiTrackPreviewResponse { Preview = result });
    }

    [HttpGet("tracks/{trackId}/licenses")]
    [ProducesResponseType(typeof(AiTrackLicenseOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLicenseOptions(string trackId)
    {
        var result = await _discovery.GetLicenseOptionsAsync(trackId);
        if (result is null) return NotFound(new { error = "Track not found." });
        return Ok(new AiTrackLicenseOptionsResponse { Licenses = result });
    }

    [HttpGet("creators/{creatorId}")]
    [ProducesResponseType(typeof(AiCreatorProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCreatorProfile(string creatorId)
    {
        var result = await _discovery.GetCreatorProfileAsync(creatorId);
        if (result is null) return NotFound(new { error = "Creator not found." });
        return Ok(new AiCreatorProfileResponse { Creator = result });
    }
}
