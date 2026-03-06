using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class CatalogController : ControllerBase
{
    private readonly ICatalogService _catalog;

    public CatalogController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> GetCatalog()
    {
        var result = await _catalog.GetCatalogAsync();
        return Ok(result);
    }

    [HttpGet("discover")]
    public async Task<IActionResult> Discover()
    {
        var result = await _catalog.GetDiscoverAsync();
        return Ok(result);
    }

    [HttpGet("tracks/{trackId}")]
    public async Task<IActionResult> GetTrack(string trackId)
    {
        var result = await _catalog.GetTrackAsync(trackId);
        return result is null ? NotFound() : Ok(result);
    }
}