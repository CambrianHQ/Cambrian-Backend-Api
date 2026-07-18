using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

public class StreamRepository : IStreamRepository
{
    private readonly CambrianDbContext _db;

    public StreamRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public async Task<Dictionary<Guid, long>> GetPlayCountsByTrackIdsAsync(IEnumerable<Guid> trackIds)
    {
        var trackIdSet = trackIds.ToHashSet();
        return await _db.TrackStats
            .AsNoTracking()
            .Where(s => trackIdSet.Contains(s.TrackId))
            .ToDictionaryAsync(s => s.TrackId, s => s.PlayCount);
    }
}
