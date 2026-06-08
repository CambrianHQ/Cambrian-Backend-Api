using Cambrian.Application.Interfaces;
using Cambrian.Application.Provenance;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Startup;

/// <summary>
/// One-off backfill that computes <see cref="Domain.Entities.Track.ContentHash"/> for
/// existing tracks (rows created before §9). Self-limiting (only touches null-hash rows),
/// idempotent, and non-fatal per track. Extracted from startup wiring so it is unit-testable.
/// </summary>
public static class TrackContentHashBackfill
{
    /// <summary>
    /// Hash up to <paramref name="maxPerRun"/> tracks whose <c>ContentHash</c> is null.
    /// Re-reads the stored audio via <see cref="IObjectStorage.OpenReadAsync(string)"/>
    /// (a track's <c>AudioUrl</c> is its storage key) and, when a <paramref name="signer"/> is
    /// supplied, also issues the free signed stamp. Tracks whose bytes can't be read are skipped
    /// and retried on a later run.
    /// </summary>
    public static async Task<TrackContentHashBackfillResult> RunAsync(
        CambrianDbContext db,
        IObjectStorage storage,
        IProvenanceSigner? signer = null,
        int maxPerRun = 1000,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var pending = await db.Tracks
            .Where(t => t.ContentHash == null && t.AudioUrl != null)
            .OrderBy(t => t.CreatedAt)
            .Take(maxPerRun + 1)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return new TrackContentHashBackfillResult(0, 0, false);

        var capped = pending.Count > maxPerRun;
        if (capped)
            pending = pending.Take(maxPerRun).ToList();

        var hashed = 0;
        var skipped = 0;

        foreach (var track in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var file = await storage.OpenReadAsync(track.AudioUrl!);
                if (file is null)
                {
                    skipped++;
                    continue;
                }

                track.ContentHash = ContentHashing.ComputeSha256Hex(file.Stream);

                // Issue the free signed stamp at the same moment we hash.
                if (signer is not null)
                {
                    var stamp = signer.Sign(track.ContentHash, DateTime.UtcNow);
                    track.Signature = stamp.Signature;
                    track.SignedAt = stamp.SignedAt;
                }

                hashed++;
            }
            catch (Exception ex)
            {
                skipped++;
                log?.Invoke($"[Backfill] Content hash failed for track {track.Id}: {ex.Message}");
            }
        }

        if (hashed > 0)
            await db.SaveChangesAsync(ct);

        return new TrackContentHashBackfillResult(hashed, skipped, capped);
    }
}

/// <summary>Outcome of a content-hash backfill run.</summary>
public readonly record struct TrackContentHashBackfillResult(int Hashed, int Skipped, bool Capped);
