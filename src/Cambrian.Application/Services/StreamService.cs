using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class StreamService : IStreamService
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;

    public StreamService(ITrackRepository tracks, IObjectStorage storage)
    {
        _tracks = tracks;
        _storage = storage;
    }

    public async Task<IReadOnlyCollection<object>> ListStreamableAsync(int take = 20)
    {
        var tracks = await _tracks.BrowseAsync();
        return tracks.Take(take).Select(t => new
        {
            id = t.Id.ToString(),
            title = t.Title,
            artist = t.Creator?.DisplayName ?? "Unknown",
            genre = t.Genre,
            duration = t.Duration,
            audioUrl = t.AudioUrl
        }).ToList<object>();
    }

    public async Task<object> GetStreamUrlAsync(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            throw new ArgumentException("trackId must be a valid GUID.");

        var track = await _tracks.GetByIdAsync(id);
        if (track?.AudioUrl is null)
            throw new KeyNotFoundException("Track not found.");

        var streamUrl = _storage.GenerateSignedUrl(track.AudioUrl);
        return new { trackId, streamUrl };
    }

}
