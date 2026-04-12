using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class LibraryRepository : ILibraryRepository
{
    private readonly CambrianDbContext _db;

    public LibraryRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<List<LibraryItem>> GetByUserIdAsync(string userId)
    {
        return await _db.Library
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.SavedAt)
            .Select(l => new LibraryItem
            {
                Id = l.Id,
                UserId = l.UserId,
                TrackId = l.TrackId,
                PurchaseId = l.PurchaseId,
                Title = l.Title,
                Artist = l.Artist,
                AudioUrl = l.AudioUrl,
                SavedAt = l.SavedAt,
                Track = l.Track == null
                    ? null!
                    : new Track
                    {
                        Id = l.Track.Id,
                        Title = l.Track.Title,
                        Genre = l.Track.Genre,
                        AudioUrl = l.Track.AudioUrl,
                        CoverArtUrl = l.Track.CoverArtUrl,
                        CreatorId = l.Track.CreatorId,
                        Creator = l.Track.Creator == null
                            ? null!
                            : new ApplicationUser
                            {
                                Id = l.Track.Creator.Id,
                                UserName = l.Track.Creator.UserName,
                                DisplayName = l.Track.Creator.DisplayName,
                                ProfileImageUrl = l.Track.Creator.ProfileImageUrl
                            }
                    }
            })
            .ToListAsync();
    }

    public async Task<LibraryItem?> GetByUserAndTrackAsync(string userId, Guid trackId)
    {
        return await _db.Library
            .FirstOrDefaultAsync(l => l.UserId == userId && l.TrackId == trackId);
    }

    public async Task AddAsync(LibraryItem item)
    {
        _db.Library.Add(item);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(LibraryItem item)
    {
        _db.Library.Update(item);
        await _db.SaveChangesAsync();
    }

    public async Task RemoveAsync(Guid id)
    {
        var item = await _db.Library.FindAsync(id);

        if (item is not null)
        {
            _db.Library.Remove(item);
            await _db.SaveChangesAsync();
        }
    }
}
