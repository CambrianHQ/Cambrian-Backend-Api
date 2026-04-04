using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Application.AI.Discovery.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("ai-discovery")]
[AllowAnonymous]
public class AiDiscoveryController : BaseController
{
    private readonly ITrackDiscoveryService _discovery;

    public AiDiscoveryController(ITrackDiscoveryService discovery)
    {
        _discovery = discovery;
    }

    /// <summary>
    /// AI-optimised track search. Supports free-text query, structured filters,
    /// and use-case intent. Returns scored, ranked results with explanations.
    /// </summary>
    [HttpGet("tracks/search")]
    public async Task<IActionResult> Search([FromQuery] SearchTracksQuery query)
    {
        if (query.PageSize is < 1 or > 100) query.PageSize = 20;
        if (query.Page < 1) query.Page = 1;

        var result = await _discovery.SearchAsync(query);
        return OkResponse(result);
    }

    /// <summary>
    /// Full track details with complete license options, creator info, and attributes.
    /// </summary>
    [HttpGet("tracks/{id}")]
    public async Task<IActionResult> GetDetails(string id)
    {
        var result = await _discovery.GetDetailsAsync(new GetTrackDetailsQuery { TrackId = id });
        if (result is null) return NotFoundResponse("Track not found.");
        return OkResponse(result);
    }

    /// <summary>
    /// All available license options for a track with pricing, allowed uses, and restrictions.
    /// </summary>
    [HttpGet("tracks/{id}/licenses")]
    public async Task<IActionResult> GetLicenses(string id)
    {
        var result = await _discovery.GetLicenseOptionsAsync(new GetLicenseOptionsQuery { TrackId = id });
        if (result.Count == 0) return NotFoundResponse("Track not found.");
        return OkResponse(result);
    }

    /// <summary>
    /// Preview info for a track (audio URL, cover art, duration).
    /// </summary>
    [HttpGet("tracks/{id}/preview")]
    public async Task<IActionResult> GetPreview(string id)
    {
        var result = await _discovery.GetDetailsAsync(new GetTrackDetailsQuery { TrackId = id });
        if (result is null) return NotFoundResponse("Track not found.");
        return OkResponse(result.Preview);
    }

    /// <summary>
    /// Creator profile with track count, follower count, and bio.
    /// </summary>
    [HttpGet("creators/{id}")]
    public async Task<IActionResult> GetCreator(string id)
    {
        var result = await _discovery.GetCreatorProfileAsync(new GetCreatorProfileQuery { Identifier = id });
        if (result is null) return NotFoundResponse("Creator not found.");
        return OkResponse(result);
    }
}
