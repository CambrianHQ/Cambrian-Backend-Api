using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("community")]
public class CommunityController : BaseController
{
    private readonly ITrackBoostService _boosts;

    public CommunityController(ITrackBoostService boosts)
    {
        _boosts = boosts;
    }

    [HttpGet]
    public IActionResult Get([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        return OkResponse(Array.Empty<object>());
    }

    /// <summary>
    /// Hot This Week — public tracks ranked by boosts in the rolling 7-day
    /// window. Anonymous-friendly (HasBoosted = false when unauthenticated).
    /// </summary>
    [HttpGet("hot-this-week")]
    [AllowAnonymous]
    public async Task<IActionResult> HotThisWeek([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _boosts.GetHotThisWeekAsync(User, page, pageSize);
        foreach (var t in result.Items)
        {
            // Route audio through the streaming proxy and cover art through the
            // /images/ proxy so raw storage object keys (bucket layout + creator
            // user IDs) never leave the backend on this anonymous route.
            if (!string.IsNullOrEmpty(t.AudioUrl))
                t.AudioUrl = ResolveAbsoluteUrl($"/stream/{t.Id}/audio");
            if (!string.IsNullOrEmpty(t.CoverArtUrl))
                t.CoverArtUrl = ResolveImageUrl(t.CoverArtUrl);
        }
        return OkResponse(result);
    }
}
