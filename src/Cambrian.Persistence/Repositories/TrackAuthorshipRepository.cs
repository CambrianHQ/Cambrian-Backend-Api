using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class TrackAuthorshipRepository : ITrackAuthorshipRepository
{
    private readonly CambrianDbContext _db;

    public TrackAuthorshipRepository(CambrianDbContext db) => _db = db;

    public Task<TrackAuthorship?> GetByTrackIdAsync(Guid trackId, CancellationToken ct = default)
        => _db.TrackAuthorships.FirstOrDefaultAsync(a => a.TrackId == trackId, ct);

    public async Task<TrackAuthorship> AddAsync(TrackAuthorship row, CancellationToken ct = default)
    {
        _db.TrackAuthorships.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task UpdateAsync(TrackAuthorship row, CancellationToken ct = default)
    {
        _db.TrackAuthorships.Update(row);
        await _db.SaveChangesAsync(ct);
    }
}
