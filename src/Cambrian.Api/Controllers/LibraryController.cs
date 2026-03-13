using Cambrian.Application.DTOs.Library;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("library")]
[Authorize]
public class LibraryController : BaseController
{
    private readonly ILibraryService _library;

    public LibraryController(ILibraryService library)
    {
        _library = library;
    }

    [HttpGet]
    public async Task<IActionResult> GetLibrary()
    {
        var items = await _library.GetLibraryAsync(User);
        // Point audioUrl at the authenticated streaming proxy so browsers
        // always get the correct Content-Type, Range support, and CORS headers.
        // Raw storage keys (e.g. "demos/audio/demo7.mp3") are never useful to the frontend.
        foreach (var item in items)
            item.AudioUrl = ResolveAbsoluteUrl($"/stream/{item.TrackId}/audio");
        return OkResponse(items);
    }

    [HttpPost]
    public async Task<IActionResult> Save(LibrarySaveRequest request)
    {
        await _library.SaveAsync(User, request);
        return CreatedResponse<object?>(null, "Track saved to library.");
    }

    [HttpDelete("{trackId}")]
    public async Task<IActionResult> Remove(string trackId)
    {
        if (!Guid.TryParse(trackId, out _))
            return ErrorResponse("trackId must be a valid GUID.");

        await _library.RemoveAsync(User, trackId);
        return MessageResponse("Track removed from library.");
    }

    [HttpPost("{trackId}")]
    public async Task<IActionResult> AddById(string trackId)
    {
        if (!Guid.TryParse(trackId, out _))
            return ErrorResponse("trackId must be a valid GUID.");

        await _library.SaveAsync(User, new LibrarySaveRequest { TrackId = trackId });
        return CreatedResponse<object?>(null, "Track saved to library.");
    }

    [HttpGet("purchased-track-ids")]
    public async Task<IActionResult> PurchasedIds()
    {
        return OkResponse(await _library.GetPurchasedTrackIdsAsync(User));
    }
}