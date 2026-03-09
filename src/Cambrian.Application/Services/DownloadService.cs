using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class DownloadService : IDownloadService
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly ILibraryRepository _library;
    private readonly IPurchaseRepository _purchases;

    public DownloadService(
        ITrackRepository tracks,
        IObjectStorage storage,
        ILibraryRepository library,
        IPurchaseRepository purchases)
    {
        _tracks = tracks;
        _storage = storage;
        _library = library;
        _purchases = purchases;
    }

    public async Task<object> GetDownloadUrlAsync(Guid trackId, string userId)
    {
        await VerifyAccessAsync(trackId, userId);

        var track = await _tracks.GetByIdAsync(trackId);
        if (track?.AudioUrl is null)
            throw new KeyNotFoundException("Track audio not found.");

        var signedUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return new { url = signedUrl };
    }

    public async Task<object> GetSignedUrlAsync(Guid trackId, string userId)
    {
        await VerifyAccessAsync(trackId, userId);

        var track = await _tracks.GetByIdAsync(trackId);
        if (track?.AudioUrl is null)
            throw new KeyNotFoundException("Track audio not found.");

        var signedUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return new { signedUrl, expiresAt = DateTime.UtcNow.AddMinutes(15) };
    }

    private async Task VerifyAccessAsync(Guid trackId, string userId)
    {
        var libraryItem = await _library.GetByUserAndTrackAsync(userId, trackId);
        if (libraryItem is null)
            throw new UnauthorizedAccessException("You must purchase this track before downloading.");

        var purchases = await _purchases.GetByBuyerIdAsync(userId);
        var hasPurchase = purchases.Any(p => p.TrackId == trackId && p.Status == "completed");
        if (!hasPurchase)
            throw new UnauthorizedAccessException("You must purchase this track before downloading.");
    }
}
