using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class StreamService : IStreamService
{
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;
    private readonly IStreamRepository _streams;

    public StreamService(ITrackRepository tracks, IObjectStorage storage, IStreamRepository streams)
    {
        _tracks = tracks;
        _storage = storage;
        _streams = streams;
    }

    public async Task<IReadOnlyCollection<object>> ListStreamableAsync(int take = 20)
    {
        var tracks = await _tracks.BrowseAsync();
        return tracks.Take(take).Select(t => new
        {
            id = t.Id.ToString(),
            title = t.Title,
            artist = t.Creator?.DisplayName ?? t.Creator?.Email ?? "Unknown",
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

    public async Task<object> StartAsync(string? trackId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(trackId) || !Guid.TryParse(trackId, out var parsedTrackId))
            throw new ArgumentException("trackId must be a valid GUID.");

        var track = await _tracks.GetByIdAsync(parsedTrackId);
        if (track?.AudioUrl is null)
            throw new KeyNotFoundException("Track not found.");

        var session = await _streams.StartAsync(parsedTrackId, userId);
        return new { streamId = session.Id.ToString(), status = "started" };
    }

    public async Task StopAsync(string? streamId)
    {
        if (string.IsNullOrWhiteSpace(streamId) || !Guid.TryParse(streamId, out var sid))
            throw new ArgumentException("streamId must be a valid GUID.");

        await _streams.StopAsync(sid);
    }
}
