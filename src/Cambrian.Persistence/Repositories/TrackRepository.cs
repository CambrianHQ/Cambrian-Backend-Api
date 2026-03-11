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
            .Include(t => t.Creator)
            .Where(t => !t.ExclusiveSold && t.Visibility == "public")
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort = null)
    {
        var query = _db.Tracks
            .Include(t => t.Creator)
            .Where(t => !t.ExclusiveSold && t.Visibility == "public");

        if (!string.IsNullOrWhiteSpace(genre))
            query = query.Where(t => t.Genre != null && t.Genre.ToLower() == genre.ToLower());

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => t.Title.ToLower().Contains(search.ToLower()));

        query = sort?.ToLower() switch
        {
            "price" => query.OrderBy(t => t.Price),
            "price_desc" => query.OrderByDescending(t => t.Price),
            "title" => query.OrderBy(t => t.Title),
            _ => query.OrderByDescending(t => t.CreatedAt)
        };

        return await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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

    public async Task<bool> TryMarkExclusiveSoldAsync(Guid trackId)
    {
        // Atomic UPDATE with WHERE clause — only succeeds if ExclusiveSold is currently false.
        // Prevents race conditions on concurrent exclusive purchase attempts.
        var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Tracks\" SET \"ExclusiveSold\" = true WHERE \"Id\" = {trackId} AND \"ExclusiveSold\" = false");
        return affected > 0;
    }
}