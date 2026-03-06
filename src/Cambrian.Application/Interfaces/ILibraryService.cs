using Cambrian.Application.DTOs.Library;

namespace Cambrian.Application.Interfaces;

public interface ILibraryService
{
    Task<IReadOnlyCollection<LibraryItemResponse>> GetLibraryAsync();

    Task SaveAsync(LibrarySaveRequest request);

    Task RemoveAsync(string trackId);
}