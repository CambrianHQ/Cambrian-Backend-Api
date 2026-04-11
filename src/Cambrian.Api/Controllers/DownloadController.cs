using Cambrian.Api.Common;
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
    private readonly IEntitlementService _entitlement;
    private readonly ILicenseCertificateRepository _licenses;
    private readonly ILogger<DownloadController> _logger;

    public DownloadController(
        ITrackRepository tracks,
        IObjectStorage storage,
        IEntitlementService entitlement,
        ILicenseCertificateRepository licenses,
        ILogger<DownloadController> logger)
    {
        _tracks = tracks;
        _storage = storage;
        _entitlement = entitlement;
        _licenses = licenses;
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

        var userId = GetRequiredUserId()!;

        // C3 — entitlement delegates to IEntitlementService (single source of truth).
        if (!await _entitlement.CanDownloadAsync(userId, id))
            return ForbiddenResponse("You must purchase this track before downloading.");

        var track = await _tracks.GetByIdAsync(id);
        if (track?.AudioUrl is null)
            return NotFoundResponse("Track audio not found.");

        _logger.LogInformation("Download requested: trackId={TrackId}", trackId);

        // Prefer the stored content type when available so legacy mislabeled keys
        // still download with the correct extension on signed-URL backends.
        string? contentType = null;
        using (var probe = await _storage.OpenReadAsync(track.AudioUrl))
        {
            if (probe is null)
                return NotFoundResponse($"Audio file not found on storage. audioUrl={track.AudioUrl}");

            contentType = probe.ContentType;
        }

        // Build a user-friendly filename from the track title
        var filename = BuildDownloadFilename(track.Title, track.AudioUrl, contentType);

        // Generate a URL with Content-Disposition: attachment so the browser
        // triggers a real "Save As" download instead of playing the audio inline.
        var url = _storage.GenerateDownloadUrl(track.AudioUrl, filename);

        // If the signed URL is relative (local storage returns /uploads/key),
        // fall back to the binary streaming endpoint so the download still works.
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = ResolveAbsoluteUrl($"/download/{trackId}/file");

        // Look up the license certificate for this track + buyer
        string? licenseId = null;
        var license = await _licenses.GetByBuyerAndTrackAsync(userId, track.CambrianTrackId);
        licenseId = license?.Id.ToString();

        return OkResponse(new { url, filename, licenseId, expiresAt = DateTime.UtcNow.AddMinutes(15) });
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

        var userId = GetRequiredUserId()!;

        // C3 — same entitlement guard.
        if (!await _entitlement.CanDownloadAsync(userId, id))
            return ForbiddenResponse("You must purchase this track before downloading.");

        var track = await _tracks.GetByIdAsync(id);
        if (track?.AudioUrl is null)
            return NotFoundResponse("Track audio not found.");

        var file = await _storage.OpenReadAsync(track.AudioUrl);
        if (file is null)
            return NotFoundResponse($"Audio file not found on storage. audioUrl={track.AudioUrl}");

        // Build a user-friendly filename from the track title
        var fileName = BuildDownloadFilename(track.Title, track.AudioUrl, file.ContentType);

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

        var userId = GetRequiredUserId()!;

        // C3 — same entitlement guard applies to the signed URL endpoint.
        if (!await _entitlement.CanDownloadAsync(userId, id))
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

    private static string BuildDownloadFilename(string? title, string? audioUrl, string? contentType = null)
    {
        var safeTitle = FilenameHelper.SanitizeFilename(title ?? "track");
        var extension = GetPreferredAudioExtension(audioUrl, contentType);
        return $"{safeTitle}{extension}";
    }

    private static string GetPreferredAudioExtension(string? audioUrl, string? contentType)
    {
        var normalizedContentType = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        var extensionFromContentType = normalizedContentType switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" or "audio/x-pn-wav" => ".wav",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/flac" => ".flac",
            "audio/aac" => ".aac",
            "audio/ogg" => ".ogg",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(extensionFromContentType))
            return extensionFromContentType;

        var extensionFromUrl = Path.GetExtension(audioUrl ?? string.Empty);
        return string.IsNullOrWhiteSpace(extensionFromUrl) ? ".mp3" : extensionFromUrl.ToLowerInvariant();
    }

}
