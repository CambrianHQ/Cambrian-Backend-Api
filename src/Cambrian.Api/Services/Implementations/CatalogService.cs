using Cambrian.Api.Data;
using Cambrian.Api.Entities;
using Cambrian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Services;

public class CatalogService : ICatalogService
{
    private readonly ApplicationDbContext _db;

    public CatalogService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IEnumerable<Track>> Discover(int page, int pageSize, string? genre, string? search)
    {
        var query = _db.Tracks.AsQueryable();

        if (!string.IsNullOrEmpty(genre))
            query = query.Where(t => t.Genre == genre);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(t => t.Title.Contains(search));

        return await query
            .OrderByDescending(t => t.UploadedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<Track>> Catalog(int page, int pageSize, string? genre, string? search)
    {
        return await Discover(page, pageSize, genre, search);
    }

    public async Task<Track?> GetTrack(string trackId)
    {
        return await _db.Tracks.FindAsync(Guid.Parse(trackId));
    }
}
