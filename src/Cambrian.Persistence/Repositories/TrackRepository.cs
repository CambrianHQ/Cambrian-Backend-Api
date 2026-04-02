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
            .Include(t => t.CreatorEntity)
            .Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public")
            .OrderByDescending(t => t.CreatedAt)
            .Take(200)
            .ToListAsync();
    }

    public async Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort = null)
    {
        return await BrowseAsync(page, pageSize, genre, search, sort, null, null, null, null);
    }

    public async Task<List<Track>> BrowseAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration)
    {
        var query = _db.Tracks
            .Include(t => t.Creator)
            .Include(t => t.CreatorEntity)
            .Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public");

        if (!string.IsNullOrWhiteSpace(genre))
            query = query.Where(t => t.Genre != null && t.Genre.ToLower() == genre.ToLower());

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => t.Title.ToLower().Contains(search.ToLower()));

        // ── New optional filters ──
        if (!string.IsNullOrWhiteSpace(mood))
            query = query.Where(t => t.Mood != null && t.Mood.ToLower() == mood.ToLower());

        if (!string.IsNullOrWhiteSpace(tempo))
            query = query.Where(t => t.Tempo != null && t.Tempo.ToLower() == tempo.ToLower());

        if (instrumental.HasValue)
            query = query.Where(t => t.Instrumental == instrumental.Value);

        if (!string.IsNullOrWhiteSpace(duration))
            query = query.Where(t => t.Duration != null && t.Duration.ToLower() == duration.ToLower());

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
            .Include(t => t.CreatorEntity)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<Track?> GetByCambrianTrackIdAsync(string cambrianTrackId)
    {
        return await _db.Tracks
            .Include(t => t.Creator)
            .Include(t => t.CreatorEntity)
            .FirstOrDefaultAsync(t => t.CambrianTrackId == cambrianTrackId);
    }

    public async Task<List<Track>> GetByCreatorIdAsync(string creatorId)
    {
        return await _db.Tracks
            .Include(t => t.Creator)
            .Include(t => t.CreatorEntity)
            .Where(t => t.CreatorId == creatorId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Track>> GetStorefrontTracksAsync(string creatorId)
    {
        return await _db.Tracks
            .Include(t => t.Creator)
            .Include(t => t.CreatorEntity)
            .Where(t => t.CreatorId == creatorId
                && t.Visibility == "public"
                && t.Status != "copyright_transferred"
                && !t.ExclusiveSold)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<int> CountAsync(string? genre = null, string? search = null,
        string? mood = null, string? tempo = null, bool? instrumental = null, string? duration = null)
    {
        var query = _db.Tracks
            .Where(t => !t.ExclusiveSold && t.Status != "copyright_transferred" && t.Visibility == "public");

        if (!string.IsNullOrWhiteSpace(genre))
            query = query.Where(t => t.Genre != null && t.Genre.ToLower() == genre.ToLower());
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => t.Title.ToLower().Contains(search.ToLower()));
        if (!string.IsNullOrWhiteSpace(mood))
            query = query.Where(t => t.Mood != null && t.Mood.ToLower() == mood.ToLower());
        if (!string.IsNullOrWhiteSpace(tempo))
            query = query.Where(t => t.Tempo != null && t.Tempo.ToLower() == tempo.ToLower());
        if (instrumental.HasValue)
            query = query.Where(t => t.Instrumental == instrumental.Value);
        if (!string.IsNullOrWhiteSpace(duration))
            query = query.Where(t => t.Duration != null && t.Duration.ToLower() == duration.ToLower());

        return await query.CountAsync();
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

    public async Task<bool> TryMarkCopyrightBuyoutAsync(Guid trackId, string buyerUserId)
    {
        // Atomic UPDATE with multi-field WHERE clause — sets all six fields in a single
        // conditional statement. Prevents race conditions on concurrent copyright buyout attempts.
        var now = DateTime.UtcNow;
        var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Tracks\" SET \"ExclusiveSold\" = true, \"Status\" = 'copyright_transferred', \"Visibility\" = 'hidden', \"OriginalCreatorId\" = \"CreatorId\", \"CopyrightOwnerId\" = {buyerUserId}, \"CopyrightTransferredAt\" = {now} WHERE \"Id\" = {trackId} AND \"ExclusiveSold\" = false AND \"Status\" != 'copyright_transferred'");
        return affected > 0;
    }
}