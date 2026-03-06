using Cambrian.Application.DTOs.Library;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class LibraryService : ILibraryService
{
    public Task<IReadOnlyCollection<LibraryItemResponse>> GetLibraryAsync()
    {
        IReadOnlyCollection<LibraryItemResponse> items =
        [
            new LibraryItemResponse
            {
                TrackId = Guid.NewGuid().ToString(),
                Title = "Owned Track",
                Artist = "Cambrian"
            }
        ];

        return Task.FromResult(items);
    }

    public Task SaveAsync(LibrarySaveRequest request)
    {
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string trackId)
    {
        return Task.CompletedTask;
    }
}