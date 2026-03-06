using Cambrian.Api.Entities;

namespace Cambrian.Api.Services.Interfaces;

public interface ICatalogService
{
    Task<IEnumerable<Track>> Discover(int page, int pageSize, string? genre, string? search);

    Task<IEnumerable<Track>> Catalog(int page, int pageSize, string? genre, string? search);

    Task<Track?> GetTrack(string trackId);
}
