using Cambrian.Application.DTOs.Stream;
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

    public async Task<IReadOnlyCollection<StreamTrackResponse>> GetTracksAsync(int take = 20)
    {
        if (take < 1)
        {
            take = 20;
        }

        var tracks = await _tracks.BrowseAsync();

        return tracks
            .Take(take)
            .Select(track => new StreamTrackResponse
            {
                Id = track.Id.ToString(),
                Title = track.Title,
                Artist = track.Creator?.DisplayName ?? track.Creator?.Email ?? "Unknown",
                Genre = track.Genre,
                Duration = track.Duration,
                AudioUrl = track.AudioUrl
            })
            .ToList();
    }

    public async Task<StreamUrlResponse?> GetStreamAsync(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
        {
            return null;
        }

        var track = await _tracks.GetByIdAsync(id);
        if (track?.AudioUrl is null)
        {
            return null;
        }

        return new StreamUrlResponse
        {
            TrackId = trackId,
            StreamUrl = _storage.GenerateSignedUrl(track.AudioUrl)
        };
    }

    public async Task<StreamStartResponse> StartAsync(string? trackId, string? userId)
    {
        var parsedTrackId = Guid.TryParse(trackId, out var id) ? id : Guid.Empty;
        var session = await _streams.StartAsync(parsedTrackId, userId);

        return new StreamStartResponse
        {
            StreamId = session.Id.ToString(),
            Status = "started"
        };
    }

    public async Task StopAsync(string? streamId)
    {
        if (Guid.TryParse(streamId, out var id))
        {
            await _streams.StopAsync(id);
        }
    }
}
