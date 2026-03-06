using System.Security.Claims;
using Cambrian.Api.Data;
using Cambrian.Api.DTOs;
using Cambrian.Api.Entities;
using Cambrian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Services;

public class LibraryService : ILibraryService
{
    private readonly ApplicationDbContext _db;

    public LibraryService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IEnumerable<string>> GetPurchasedTrackIds(ClaimsPrincipal user)
    {
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        return await _db.Purchases
            .Where(p => p.UserId == userId && p.Paid)
            .Select(p => p.TrackId.ToString())
            .ToListAsync();
    }

    public async Task SaveTrack(ClaimsPrincipal user, LibrarySaveRequest request)
    {
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TrackId = Guid.Parse(request.TrackId),
            Paid = true
        });

        await _db.SaveChangesAsync();
    }

    public async Task RemoveTrack(ClaimsPrincipal user, string trackId)
    {
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var purchase = await _db.Purchases
            .FirstOrDefaultAsync(p => p.TrackId == Guid.Parse(trackId) && p.UserId == userId);

        if (purchase != null)
        {
            _db.Purchases.Remove(purchase);
            await _db.SaveChangesAsync();
        }
    }
}
