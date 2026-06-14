using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

/// <summary>
/// EF Core data access for <see cref="MasteringJob"/>. Mirrors the
/// <see cref="TrackBoostRepository"/> style (thin, owner-scoped queries).
/// </summary>
public class MasteringJobRepository : IMasteringJobRepository
{
    private readonly CambrianDbContext _db;

    public MasteringJobRepository(CambrianDbContext db)
    {
        _db = db;
    }

    public Task<MasteringJob?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.MasteringJobs.FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<MasteringJob?> GetForOwnerAsync(Guid id, string creatorId, CancellationToken ct = default) =>
        _db.MasteringJobs.FirstOrDefaultAsync(j => j.Id == id && j.CreatorId == creatorId, ct);

    public async Task AddAsync(MasteringJob job, CancellationToken ct = default)
    {
        _db.MasteringJobs.Add(job);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(MasteringJob job, CancellationToken ct = default)
    {
        _db.MasteringJobs.Update(job);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MasteringJob>> ListByCreatorAsync(string creatorId, int take, CancellationToken ct = default)
    {
        var capped = take <= 0 ? 20 : Math.Min(take, 100);
        return await _db.MasteringJobs
            .Where(j => j.CreatorId == creatorId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(capped)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Count MONTHLY credit-charged, non-failed jobs since the month start. Failed jobs
    /// are excluded so a terminal failure releases the credit (the audit row stays).
    /// Purchased-funded charges are excluded — they draw from the never-expiring pool,
    /// not the monthly allowance (legacy null source counts as monthly).
    /// </summary>
    public Task<int> CountChargedThisMonthAsync(string creatorId, DateTime monthStartUtc, CancellationToken ct = default) =>
        _db.MasteringJobs.CountAsync(
            j => j.CreatorId == creatorId
                 && j.ChargedAt != null
                 && j.ChargedAt >= monthStartUtc
                 && j.Status != "failed"
                 && (j.CreditSource == null || j.CreditSource != "purchased"),
            ct);

    /// <summary>
    /// Count non-failed jobs funded from the PURCHASED credit pool (all time). The
    /// remaining purchased balance is SUM(purchased credits) − this count.
    /// </summary>
    public Task<int> CountPurchasedConsumedAsync(string creatorId, CancellationToken ct = default) =>
        _db.MasteringJobs.CountAsync(
            j => j.CreatorId == creatorId
                 && j.CreditSource == "purchased"
                 && j.Status != "failed",
            ct);

    public Task<MasteringJob?> GetLatestForTrackAsync(Guid trackId, CancellationToken ct = default) =>
        _db.MasteringJobs
            .Where(j => j.TrackId == trackId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<MasteringJob?> GetActiveByTrackAndHashAsync(Guid trackId, string contentHash, CancellationToken ct = default) =>
        _db.MasteringJobs
            .Where(j => j.TrackId == trackId
                        && j.ContentHash == contentHash
                        && j.Status != "failed")
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Race-safely claim the oldest queued job for the worker. A conditional
    /// <c>ExecuteUpdateAsync</c> (status guard) inside a transaction guarantees that
    /// two worker ticks (or instances) can never both claim the same row: only the
    /// update whose <c>WHERE Status = 'queued'</c> still matches flips it to
    /// <c>processing</c>. Returns null when the queue is empty.
    /// </summary>
    public async Task<MasteringJob?> ClaimNextQueuedAsync(CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Oldest queued candidate.
            var candidateId = await _db.MasteringJobs
                .Where(j => j.Status == "queued")
                .OrderBy(j => j.CreatedAt)
                .Select(j => (Guid?)j.Id)
                .FirstOrDefaultAsync(ct);

            if (candidateId is null)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var now = DateTime.UtcNow;

            // Conditional flip — guarded on Status so a concurrent claim loses the race.
            var claimed = await _db.MasteringJobs
                .Where(j => j.Id == candidateId.Value && j.Status == "queued")
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.Status, "processing")
                          .SetProperty(j => j.StartedAt, now),
                    ct);

            if (claimed == 0)
            {
                // Someone else claimed it between our read and update.
                await tx.RollbackAsync(ct);
                return null;
            }

            await tx.CommitAsync(ct);

            // Return a fresh (tracked) copy reflecting the claimed state.
            return await _db.MasteringJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == candidateId.Value, ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
