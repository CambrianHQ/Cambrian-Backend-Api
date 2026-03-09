using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Cambrian.Application.DTOs.Library;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class LibraryService : ILibraryService
{
    private readonly ILibraryRepository _library;
    private readonly IPurchaseRepository _purchases;
    private readonly ITrackRepository _tracks;

    public LibraryService(ILibraryRepository library, IPurchaseRepository purchases, ITrackRepository tracks)
    {
        _library = library;
        _purchases = purchases;
        _tracks = tracks;
    }

    public async Task<IReadOnlyCollection<LibraryItemResponse>> GetLibraryAsync(ClaimsPrincipal user)
    {
        var userId = GetUserId(user);
        var items = await _library.GetByUserIdAsync(userId);

        var purchases = await _purchases.GetByBuyerIdAsync(userId) ?? new List<Purchase>();
        var completedPurchases = purchases
            .Where(p => p.Status == "completed")
            .GroupBy(p => p.TrackId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.CreatedAt).First());

        return items.Select(i =>
        {
            completedPurchases.TryGetValue(i.TrackId, out var purchase);

            return new LibraryItemResponse
            {
                TrackId = i.TrackId.ToString(),
                Title = i.Track?.Title ?? i.Title ?? "",
                Artist = i.Track?.Creator?.DisplayName ?? i.Artist ?? "",
                Purchased = purchase is not null,
                PurchasedOn = purchase?.CreatedAt.ToString("o"),
                AudioUrl = i.Track?.AudioUrl ?? i.AudioUrl,
                Genre = i.Track?.Genre
            };
        }).ToList();
    }

    public async Task SaveAsync(ClaimsPrincipal user, LibrarySaveRequest request)
    {
        var userId = GetUserId(user);
        var trackId = Guid.Parse(request.TrackId);

        // Prevent duplicates
        var existing = await _library.GetByUserAndTrackAsync(userId, trackId);
        if (existing is not null)
            return;

        var track = await _tracks.GetByIdAsync(trackId)
                    ?? throw new KeyNotFoundException($"Track {request.TrackId} not found.");

        var item = new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TrackId = trackId,
            Title = track.Title,
            Artist = track.Creator?.DisplayName,
            AudioUrl = track.AudioUrl
        };

        await _library.AddAsync(item);
    }

    public async Task RemoveAsync(ClaimsPrincipal user, string trackId)
    {
        var userId = GetUserId(user);
        var item = await _library.GetByUserAndTrackAsync(userId, Guid.Parse(trackId));

        if (item is not null)
            await _library.RemoveAsync(item.Id);
    }

    public async Task<IReadOnlyCollection<string>> GetPurchasedTrackIdsAsync(ClaimsPrincipal user)
    {
        var userId = GetUserId(user);
        var purchases = await _purchases.GetByBuyerIdAsync(userId);

        return purchases
            .Where(p => p.Status == "completed")
            .Select(p => p.TrackId.ToString())
            .Distinct()
            .ToList();
    }

    private static string GetUserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? throw new UnauthorizedAccessException("No user identity found.");
}