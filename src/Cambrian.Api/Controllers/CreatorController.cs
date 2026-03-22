using Cambrian.Api.Middleware;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("creator")]
[Authorize]
[RequireCreatorTier]
public class CreatorController : BaseController
{
    private readonly ICreatorService _creator;

    public CreatorController(ICreatorService creator)
    {
        _creator = creator;
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> Tracks([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;

        var userId = GetRequiredUserId()!;
        var tracks = await _creator.GetTracksAsync(userId, page, pageSize);
        return OkResponse(tracks);
    }

    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue()
    {
        var userId = GetRequiredUserId()!;
        var revenue = await _creator.GetRevenueAsync(userId);
        return OkResponse(revenue);
    }
}
