using Cambrian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("stream")]
public class StreamController : BaseController
{
    private readonly ICatalogService _catalog;

    public StreamController(ICatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpGet("{trackId}")]
    public async Task<IActionResult> Stream(string trackId)
    {
        if (!Guid.TryParse(trackId, out _))
            return ErrorResponse("trackId must be a valid GUID.");

        var track = await _catalog.GetTrack(trackId);
        if (track is null)
            return NotFoundResponse($"Track '{trackId}' not found.");

        var path = $"uploads/{track.AudioUrl}";
        if (!System.IO.File.Exists(path))
            return NotFoundResponse("Audio file not available.");

        var stream = System.IO.File.OpenRead(path);
        return File(stream, "audio/mpeg", enableRangeProcessing: true);
    }
}
