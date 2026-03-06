using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("download")]
[Authorize]
public class DownloadController : BaseController
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly ILibraryRepository _library;

    public DownloadController(ITrackRepository tracks, IObjectStorage storage, ILibraryRepository library)
    {
        _tracks = tracks;
        _storage = storage;
        _library = library;
    }

    [HttpGet("{trackId}")]
    public async Task<IActionResult> Download(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return ErrorResponse("trackId must be a valid GUID.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Verify user owns this track in their library
        var libraryItem = await _library.GetByUserAndTrackAsync(userId, id);
        if (libraryItem is null)
            return ForbiddenResponse("You must purchase this track before downloading.");

        var track = await _tracks.GetByIdAsync(id);
        if (track?.AudioUrl is null)
            return NotFoundResponse("Track audio not found.");

        var signedUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return OkResponse(new { url = signedUrl });
    }

    [HttpGet("{trackId}/signed")]
    public async Task<IActionResult> SignedUrl(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return ErrorResponse("trackId must be a valid GUID.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var libraryItem = await _library.GetByUserAndTrackAsync(userId, id);
        if (libraryItem is null)
            return ForbiddenResponse("You must purchase this track before downloading.");

        var track = await _tracks.GetByIdAsync(id);
        if (track?.AudioUrl is null)
            return NotFoundResponse("Track audio not found.");

        var signedUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return OkResponse(new { signedUrl, expiresAt = DateTime.UtcNow.AddMinutes(15) });
    }
}
