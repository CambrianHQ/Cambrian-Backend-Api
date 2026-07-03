using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cambrian.Persistence.Repositories;

/// <summary>
/// EF Core data access for <see cref="MasteringJob"/>. Mirrors the
/// <see cref="TrackBoostRepository"/> style (thin, owner-scoped queries).
/// </summary>
public class MasteringJobRepository : IMasteringJobRepository
{
    private readonly CambrianDbContext _db;
    private readonly MasteringOptions _options;
    private readonly TimeProvider _time;

    public MasteringJobRepository(CambrianDbContext db, IOptions<MasteringOptions> options, TimeProvider time)
    {
        _db = db;
        _options = options.Value;
        _time = time;
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
    /// Purchased-funded charges are excluded - they draw from the never-expiring pool,
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
    /// remaining purchased balance is SUM(purchased credits) - this count.
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

    public Task<MasteringJob?> GetActiveClassicByCreatorAndHashAsync(
        string creatorId,
        string contentHash,
        CancellationToken ct = default) =>
        _db.MasteringJobs
            .Where(j => j.CreatorId == creatorId
                        && j.Kind == "mastering"
                        && j.ContentHash == contentHash
                        && j.Status != "failed")
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Race-safely claim the oldest queued or stale processing job. The lease columns
    /// make worker ownership durable across app crashes: active processing jobs are
    /// skipped, expired jobs are either reclaimed or failed once retry policy is spent.
    /// </summary>
    public async Task<MasteringJob?> ClaimNextQueuedAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var now = UtcNow();
                var leaseId = Guid.NewGuid();
                var leaseExpiresAt = now.AddSeconds(LeaseSeconds);
                var maxRetries = MaxRetryCount;

                var candidate = await _db.MasteringJobs
                    .Where(j => j.Status == "queued"
                                || (j.Status == "processing"
                                    && (j.ProcessingLeaseExpiresAt == null || j.ProcessingLeaseExpiresAt <= now)))
                    .OrderBy(j => j.CreatedAt)
                    .Select(j => new { j.Id, j.Status, j.RetryCount })
                    .FirstOrDefaultAsync(ct);

                if (candidate is null)
                {
                    await tx.RollbackAsync(ct);
                    return null;
                }

                if (candidate.Status == "processing" && candidate.RetryCount >= maxRetries)
                {
                    await _db.MasteringJobs
                        .Where(j => j.Id == candidate.Id
                                    && j.Status == "processing"
                                    && (j.ProcessingLeaseExpiresAt == null || j.ProcessingLeaseExpiresAt <= now)
                                    && j.RetryCount == candidate.RetryCount)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(j => j.Status, "failed")
                                  .SetProperty(j => j.Error, "Processing lease expired after retry policy was exhausted.")
                                  .SetProperty(j => j.CompletedAt, now)
                                  .SetProperty(j => j.ProcessingLeaseId, (Guid?)null)
                                  .SetProperty(j => j.ProcessingLeaseExpiresAt, (DateTime?)null)
                                  .SetProperty(j => j.LastHeartbeatAt, now),
                            ct);

                    await tx.CommitAsync(ct);
                    continue;
                }

                var claimed = candidate.Status == "queued"
                    ? await ClaimQueuedAsync(candidate.Id, leaseId, now, leaseExpiresAt, ct)
                    : await ReclaimExpiredProcessingAsync(candidate.Id, candidate.RetryCount, leaseId, now, leaseExpiresAt, ct);

                if (claimed == 0)
                {
                    await tx.RollbackAsync(ct);
                    continue;
                }

                await tx.CommitAsync(ct);

                return await _db.MasteringJobs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(j => j.Id == candidate.Id, ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        return null;
    }

    public async Task<bool> HeartbeatAsync(Guid jobId, Guid leaseId, CancellationToken ct = default)
    {
        var now = UtcNow();
        var leaseExpiresAt = now.AddSeconds(LeaseSeconds);

        var updated = await _db.MasteringJobs
            .Where(j => j.Id == jobId
                        && j.Status == "processing"
                        && j.ProcessingLeaseId == leaseId
                        && j.ProcessingLeaseExpiresAt != null
                        && j.ProcessingLeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.LastHeartbeatAt, now)
                      .SetProperty(j => j.ProcessingLeaseExpiresAt, leaseExpiresAt),
                ct);

        return updated == 1;
    }

    public async Task<bool> UpdateLeaseOwnedAsync(MasteringJob job, Guid leaseId, CancellationToken ct = default)
    {
        var current = await GetActiveLeaseOwnedAsync(job.Id, leaseId, ct);
        if (current is null)
            return false;

        current.MasteredWavKey = job.MasteredWavKey;
        current.MasteredMp3Key = job.MasteredMp3Key;
        current.PreviewKey = job.PreviewKey;
        current.EngineRef = job.EngineRef;
        current.InputLufs = job.InputLufs;
        current.OutputLufs = job.OutputLufs;
        current.OutputTruePeakDbtp = job.OutputTruePeakDbtp;
        current.Stage = job.Stage;
        current.StageHistoryJson = job.StageHistoryJson;
        current.Error = job.Error;
        current.LastHeartbeatAt = UtcNow();

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> MarkAwaitingApprovalAsync(MasteringJob job, Guid leaseId, CancellationToken ct = default)
    {
        var now = UtcNow();
        var updated = await _db.MasteringJobs
            .Where(j => j.Id == job.Id
                        && j.Status == "processing"
                        && j.ProcessingLeaseId == leaseId
                        && j.ProcessingLeaseExpiresAt != null
                        && j.ProcessingLeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "awaiting_approval")
                      .SetProperty(j => j.PreviewKey, job.PreviewKey)
                      .SetProperty(j => j.EngineRef, job.EngineRef)
                      .SetProperty(j => j.Error, (string?)null)
                      .SetProperty(j => j.ProcessingLeaseId, (Guid?)null)
                      .SetProperty(j => j.ProcessingLeaseExpiresAt, (DateTime?)null)
                      .SetProperty(j => j.LastHeartbeatAt, now),
                ct);

        return updated == 1;
    }

    public async Task<bool> MarkDoneAsync(MasteringJob job, Guid leaseId, CancellationToken ct = default)
    {
        var now = UtcNow();
        var updated = await _db.MasteringJobs
            .Where(j => j.Id == job.Id
                        && j.Status == "processing"
                        && j.ProcessingLeaseId == leaseId
                        && j.ProcessingLeaseExpiresAt != null
                        && j.ProcessingLeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "done")
                      .SetProperty(j => j.CompletedAt, now)
                      .SetProperty(j => j.Error, (string?)null)
                      .SetProperty(j => j.MasteredWavKey, job.MasteredWavKey)
                      .SetProperty(j => j.MasteredMp3Key, job.MasteredMp3Key)
                      .SetProperty(j => j.InputLufs, job.InputLufs)
                      .SetProperty(j => j.OutputLufs, job.OutputLufs)
                      .SetProperty(j => j.OutputTruePeakDbtp, job.OutputTruePeakDbtp)
                      .SetProperty(j => j.Stage, job.Stage)
                      .SetProperty(j => j.StageHistoryJson, job.StageHistoryJson)
                      .SetProperty(j => j.ProcessingLeaseId, (Guid?)null)
                      .SetProperty(j => j.ProcessingLeaseExpiresAt, (DateTime?)null)
                      .SetProperty(j => j.LastHeartbeatAt, now),
                ct);

        return updated == 1;
    }

    public async Task<bool> RequeueForRetryAsync(
        Guid jobId,
        Guid leaseId,
        int retryCount,
        string error,
        CancellationToken ct = default)
    {
        var now = UtcNow();
        var updated = await _db.MasteringJobs
            .Where(j => j.Id == jobId
                        && j.Status == "processing"
                        && j.ProcessingLeaseId == leaseId
                        && j.ProcessingLeaseExpiresAt != null
                        && j.ProcessingLeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "queued")
                      .SetProperty(j => j.StartedAt, (DateTime?)null)
                      .SetProperty(j => j.ProcessingLeaseId, (Guid?)null)
                      .SetProperty(j => j.ProcessingLeaseExpiresAt, (DateTime?)null)
                      .SetProperty(j => j.LastHeartbeatAt, now)
                      .SetProperty(j => j.Error, error)
                      .SetProperty(j => j.RetryCount, retryCount),
                ct);

        return updated == 1;
    }

    public async Task<bool> MarkFailedAsync(Guid jobId, Guid leaseId, string error, CancellationToken ct = default)
    {
        var now = UtcNow();
        var updated = await _db.MasteringJobs
            .Where(j => j.Id == jobId
                        && j.Status == "processing"
                        && j.ProcessingLeaseId == leaseId
                        && j.ProcessingLeaseExpiresAt != null
                        && j.ProcessingLeaseExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "failed")
                      .SetProperty(j => j.Error, error)
                      .SetProperty(j => j.CompletedAt, now)
                      .SetProperty(j => j.ProcessingLeaseId, (Guid?)null)
                      .SetProperty(j => j.ProcessingLeaseExpiresAt, (DateTime?)null)
                      .SetProperty(j => j.LastHeartbeatAt, now),
                ct);

        return updated == 1;
    }

    private Task<MasteringJob?> GetActiveLeaseOwnedAsync(Guid jobId, Guid leaseId, CancellationToken ct)
    {
        var now = UtcNow();
        return _db.MasteringJobs.FirstOrDefaultAsync(
            j => j.Id == jobId
                 && j.Status == "processing"
                 && j.ProcessingLeaseId == leaseId
                 && j.ProcessingLeaseExpiresAt != null
                 && j.ProcessingLeaseExpiresAt > now,
            ct);
    }

    private Task<int> ClaimQueuedAsync(
        Guid jobId,
        Guid leaseId,
        DateTime now,
        DateTime leaseExpiresAt,
        CancellationToken ct) =>
        _db.MasteringJobs
            .Where(j => j.Id == jobId && j.Status == "queued")
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "processing")
                      .SetProperty(j => j.StartedAt, now)
                      .SetProperty(j => j.ProcessingStartedAt, now)
                      .SetProperty(j => j.LastHeartbeatAt, now)
                      .SetProperty(j => j.ProcessingLeaseId, leaseId)
                      .SetProperty(j => j.ProcessingLeaseExpiresAt, leaseExpiresAt),
                ct);

    private Task<int> ReclaimExpiredProcessingAsync(
        Guid jobId,
        int currentRetryCount,
        Guid leaseId,
        DateTime now,
        DateTime leaseExpiresAt,
        CancellationToken ct) =>
        _db.MasteringJobs
            .Where(j => j.Id == jobId
                        && j.Status == "processing"
                        && (j.ProcessingLeaseExpiresAt == null || j.ProcessingLeaseExpiresAt <= now)
                        && j.RetryCount == currentRetryCount)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "processing")
                      .SetProperty(j => j.StartedAt, now)
                      .SetProperty(j => j.ProcessingStartedAt, now)
                      .SetProperty(j => j.LastHeartbeatAt, now)
                      .SetProperty(j => j.ProcessingLeaseId, leaseId)
                      .SetProperty(j => j.ProcessingLeaseExpiresAt, leaseExpiresAt)
                      .SetProperty(j => j.RetryCount, currentRetryCount + 1)
                      .SetProperty(j => j.Error, "Recovered expired processing lease; retrying."),
                ct);

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private int LeaseSeconds => Math.Max(30, _options.ProcessingLeaseSeconds);

    private int MaxRetryCount => Math.Max(0, _options.MaxRetryCount);
}
