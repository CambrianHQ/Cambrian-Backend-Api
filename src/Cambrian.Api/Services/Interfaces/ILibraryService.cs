using System.Security.Claims;
using Cambrian.Api.DTOs;

namespace Cambrian.Api.Services.Interfaces;

public interface ILibraryService
{
    Task<IEnumerable<string>> GetPurchasedTrackIds(ClaimsPrincipal user);

    Task SaveTrack(ClaimsPrincipal user, LibrarySaveRequest request);

    Task RemoveTrack(ClaimsPrincipal user, string trackId);
}
