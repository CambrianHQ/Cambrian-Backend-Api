using Cambrian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("stream")]
public class StreamController : ControllerBase
{
    private readonly ICatalogService _catalog;

    public StreamController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpGet("{trackId}")]
    public async Task<IActionResult> Stream(string trackId)
    {
        var track = await _catalog.GetTrack(trackId);

        if (track == null)
            return NotFound();

        var path = $"uploads/{track.AudioUrl}";

        if (!System.IO.File.Exists(path))
            return NotFound();

        var stream = System.IO.File.OpenRead(path);

        return File(stream, "audio/mpeg", enableRangeProcessing: true);
    }
}
