using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface ILibraryRepository
{
    Task<List<LibraryItem>> GetByUserIdAsync(string userId);

    Task<LibraryItem?> GetByUserAndTrackAsync(string userId, Guid trackId);

    Task AddAsync(LibraryItem item);

    Task RemoveAsync(Guid id);
}
