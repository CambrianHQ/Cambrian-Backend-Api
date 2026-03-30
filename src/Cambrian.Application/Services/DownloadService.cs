using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class DownloadService : IDownloadService
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly IEntitlementService _entitlement;

    public DownloadService(ITrackRepository tracks, IObjectStorage storage, IEntitlementService entitlement)
    {
        _tracks = tracks;
        _storage = storage;
        _entitlement = entitlement;
    }

    public async Task<object> GetDownloadUrlAsync(Guid trackId, string userId)
    {
        if (!await _entitlement.CanDownloadAsync(userId, trackId))
            throw new UnauthorizedAccessException("You must purchase this track before downloading.");

        var track = await _tracks.GetByIdAsync(trackId);
        if (track?.AudioUrl is null)
            throw new KeyNotFoundException("Track audio not found.");

        var signedUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return new { url = signedUrl };
    }

    public async Task<object> GetSignedUrlAsync(Guid trackId, string userId)
    {
        if (!await _entitlement.CanDownloadAsync(userId, trackId))
            throw new UnauthorizedAccessException("You must purchase this track before downloading.");

        var track = await _tracks.GetByIdAsync(trackId);
        if (track?.AudioUrl is null)
            throw new KeyNotFoundException("Track audio not found.");

        var signedUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return new { signedUrl, expiresAt = DateTime.UtcNow.AddMinutes(15) };
    }
}
