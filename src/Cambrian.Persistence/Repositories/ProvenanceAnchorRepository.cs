using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public sealed class ProvenanceAnchorRepository : IProvenanceAnchorRepository
{
    private readonly CambrianDbContext _db;

    public ProvenanceAnchorRepository(CambrianDbContext db) => _db = db;

    public Task<ProvenanceAnchor?> GetByTrackIdAsync(Guid trackId, CancellationToken ct = default)
        => _db.ProvenanceAnchors.FirstOrDefaultAsync(a => a.TrackId == trackId, ct);

    public async Task<ProvenanceAnchor> AddAsync(ProvenanceAnchor row, CancellationToken ct = default)
    {
        _db.ProvenanceAnchors.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task UpdateAsync(ProvenanceAnchor row, CancellationToken ct = default)
    {
        _db.ProvenanceAnchors.Update(row);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<ProvenanceAnchor>> GetPendingAsync(int max, CancellationToken ct = default)
        => _db.ProvenanceAnchors
            .Where(a => a.Status == "pending")
            .OrderBy(a => a.CreatedAt)
            .Take(max)
            .ToListAsync(ct);

    public async Task UpdateRangeAsync(IEnumerable<ProvenanceAnchor> rows, CancellationToken ct = default)
    {
        _db.ProvenanceAnchors.UpdateRange(rows);
        await _db.SaveChangesAsync(ct);
    }
}
