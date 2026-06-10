using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

/// <summary>EF Core data access for <see cref="AuthorshipRecord"/> (thin, owner-scoped queries).</summary>
public class AuthorshipRecordRepository : IAuthorshipRecordRepository
{
    private readonly CambrianDbContext _db;

    public AuthorshipRecordRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public Task<AuthorshipRecord?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.AuthorshipRecords.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<AuthorshipRecord?> GetForOwnerAsync(Guid id, string creatorId, CancellationToken ct = default) =>
        _db.AuthorshipRecords.FirstOrDefaultAsync(r => r.Id == id && r.CreatorId == creatorId, ct);

    public async Task AddAsync(AuthorshipRecord record, CancellationToken ct = default)
    {
        _db.AuthorshipRecords.Add(record);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AuthorshipRecord record, CancellationToken ct = default)
    {
        _db.AuthorshipRecords.Update(record);
        await _db.SaveChangesAsync(ct);
    }
}
