using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class DownloadService : IDownloadService
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly ILibraryRepository _library;

    public DownloadService(ITrackRepository tracks, IObjectStorage storage, ILibraryRepository library)
    {
        _tracks = tracks;
        _storage = storage;
        _library = library;
    }

    public async Task<object> GetDownloadUrlAsync(Guid trackId, string userId)
    {
        var libraryItem = await _library.GetByUserAndTrackAsync(userId, trackId);
        if (libraryItem is null)
            throw new UnauthorizedAccessException("You must purchase this track before downloading.");

        var track = await _tracks.GetByIdAsync(trackId);
        if (track?.AudioUrl is null)
            throw new KeyNotFoundException("Track audio not found.");

        var signedUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return new { url = signedUrl };
    }

    public async Task<object> GetSignedUrlAsync(Guid trackId, string userId)
    {
        var libraryItem = await _library.GetByUserAndTrackAsync(userId, trackId);
        if (libraryItem is null)
            throw new UnauthorizedAccessException("You must purchase this track before downloading.");

        var track = await _tracks.GetByIdAsync(trackId);
        if (track?.AudioUrl is null)
            throw new KeyNotFoundException("Track audio not found.");

        var signedUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return new { signedUrl, expiresAt = DateTime.UtcNow.AddMinutes(15) };
    }
}
