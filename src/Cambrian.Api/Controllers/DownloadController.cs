using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cambrian.Api.Controllers;

[Route("download")]
[Authorize]
public class DownloadController : BaseController
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly ILibraryRepository _library;
    private readonly ILogger<DownloadController> _logger;

    public DownloadController(ITrackRepository tracks, IObjectStorage storage, ILibraryRepository library, ILogger<DownloadController> logger)
    {
        _tracks = tracks;
        _storage = storage;
        _library = library;
        _logger = logger;
    }

    /// <summary>
    /// Validates purchase entitlement and returns a download URL.
    /// The frontend creates an anchor element pointing at this URL to trigger
    /// a browser-native download (with Content-Disposition / filename).
    /// For S3/R2 this is a pre-signed URL; for local storage it falls back
    /// to the binary streaming endpoint.
    /// </summary>
    [HttpGet("{trackId}")]
    public async Task<IActionResult> Download(string trackId)
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

        _logger.LogInformation("Download requested: trackId={TrackId}, audioUrl={AudioUrl}", trackId, track.AudioUrl);

        var url = _storage.GenerateSignedUrl(track.AudioUrl);

        // If the signed URL is relative (local storage returns /uploads/key),
        // fall back to the binary streaming endpoint so the download still works.
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = ResolveAbsoluteUrl($"/download/{trackId}/file");

        return OkResponse(new { url, expiresAt = DateTime.UtcNow.AddMinutes(15) });
    }

    /// <summary>
    /// Streams the raw audio bytes. Used as a fallback for local storage
    /// where pre-signed URLs aren't available, and for direct browser downloads
    /// via the URL returned by the GET /download/{trackId} endpoint.
    /// </summary>
    [HttpGet("{trackId}/file")]
    public async Task<IActionResult> DownloadFile(string trackId)
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

        var file = await _storage.OpenReadAsync(track.AudioUrl);
        if (file is null)
            return NotFoundResponse($"Audio file not found on storage. audioUrl={track.AudioUrl}");

        // Build a user-friendly filename from the track title
        var ext = Path.GetExtension(track.AudioUrl) ?? ".mp3";
        var safeTitle = string.Concat(
            (track.Title ?? "track").Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "track";
        var fileName = $"{safeTitle}{ext}";

        Response.Headers["Cache-Control"] = "private, no-store";
        return File(file.Stream, file.ContentType, fileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// Returns a pre-signed URL for direct download. Works best with S3/R2 storage.
    /// For local storage, redirects to the streaming download endpoint above.
    /// </summary>
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

        // If the signed URL is relative (local storage), make it absolute
        // so the frontend can always treat it as a full URL.
        if (!signedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            signedUrl = ResolveAbsoluteUrl($"/download/{trackId}/file");

        return OkResponse(new { signedUrl, expiresAt = DateTime.UtcNow.AddMinutes(15) });
    }
}
