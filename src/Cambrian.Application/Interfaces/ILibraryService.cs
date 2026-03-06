using System.Security.Claims;
using Cambrian.Application.DTOs.Library;

namespace Cambrian.Application.Interfaces;

public interface ILibraryService
{
    Task<IReadOnlyCollection<LibraryItemResponse>> GetLibraryAsync(ClaimsPrincipal user);

    Task SaveAsync(ClaimsPrincipal user, LibrarySaveRequest request);

    Task RemoveAsync(ClaimsPrincipal user, string trackId);

    Task<IReadOnlyCollection<string>> GetPurchasedTrackIdsAsync(ClaimsPrincipal user);
}