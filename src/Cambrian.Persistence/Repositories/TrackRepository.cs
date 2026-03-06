using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class TrackRepository : ITrackRepository
{
    private readonly CambrianDbContext _db;

    public TrackRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<List<Track>> BrowseAsync()
    {
        return await _db.Tracks
            .Where(t => !t.ExclusiveSold && t.Visibility == "public")
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<Track?> GetByIdAsync(Guid id)
    {
        return await _db.Tracks
            .Include(t => t.Creator)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<List<Track>> GetByCreatorIdAsync(string creatorId)
    {
        return await _db.Tracks
            .Where(t => t.CreatorId == creatorId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task AddAsync(Track track)
    {
        _db.Tracks.Add(track);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Track track)
    {
        _db.Tracks.Update(track);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var track = await _db.Tracks.FindAsync(id);

        if (track is not null)
        {
            _db.Tracks.Remove(track);
            await _db.SaveChangesAsync();
        }
    }
}
