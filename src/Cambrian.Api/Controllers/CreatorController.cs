using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("creator")]
[Authorize(Roles = "Creator")]
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
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var tracks = await _creator.GetTracksAsync(userId, page, pageSize);
        return OkResponse(tracks);
    }

    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var revenue = await _creator.GetRevenueAsync(userId);
        return OkResponse(revenue);
    }
}
