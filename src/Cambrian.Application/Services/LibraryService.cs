using System.Security.Claims;
using Cambrian.Application.DTOs.Library;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class LibraryService : ILibraryService
{
    public Task<IReadOnlyCollection<LibraryItemResponse>> GetLibraryAsync(ClaimsPrincipal user)
    {
        // TODO: Filter by authenticated user ID
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

    public Task SaveAsync(ClaimsPrincipal user, LibrarySaveRequest request)
    {
        // TODO: Save to user's library
        return Task.CompletedTask;
    }

    public Task RemoveAsync(ClaimsPrincipal user, string trackId)
    {
        // TODO: Remove from user's library
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> GetPurchasedTrackIdsAsync(ClaimsPrincipal user)
    {
        // TODO: Return purchased track IDs for authenticated user
        IReadOnlyCollection<string> ids = Array.Empty<string>();
        return Task.FromResult(ids);
    }
}