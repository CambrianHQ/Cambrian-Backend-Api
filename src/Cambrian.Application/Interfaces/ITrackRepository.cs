using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface ITrackRepository
{
    Task<List<Track>> BrowseAsync();

    Task<Track?> GetByIdAsync(Guid id);

    Task<List<Track>> GetByCreatorIdAsync(string creatorId);

    Task AddAsync(Track track);

    Task UpdateAsync(Track track);

    Task DeleteAsync(Guid id);
}
