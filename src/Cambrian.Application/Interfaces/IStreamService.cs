using Cambrian.Application.DTOs.Stream;

namespace Cambrian.Application.Interfaces;

public interface IStreamService
{
    Task<IReadOnlyCollection<StreamTrackResponse>> GetTracksAsync(int take = 20);

    Task<StreamUrlResponse?> GetStreamAsync(string trackId);

    Task<StreamStartResponse> StartAsync(string? trackId, string? userId);

    Task StopAsync(string? streamId);
}
