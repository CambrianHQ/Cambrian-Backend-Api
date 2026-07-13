namespace Cambrian.Domain.Entities;

public class StreamSession
{
    public Guid Id { get; set; }

    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    public string? UserId { get; set; }

    public string? Title { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StoppedAt { get; set; }

    /// <summary>
    /// Deterministic dedupe key for this play attempt (e.g. "user:{userId}:{trackId}:{startBucket}"
    /// or "anon:{hashedClientKey}:{trackId}:{hourBucket}"). Enforced UNIQUE at the database level
    /// so a retried request, a duplicate anonymous ping, or the same request landing on two backend
    /// replicas all resolve to the same durable row instead of double-counting — the constraint is
    /// what "cannot process one event twice" actually means for this table, not a plain read-then-write
    /// check. Legacy rows created before this column existed get a synthesized "legacy:{Id}" value
    /// (see the migration) so the NOT NULL + UNIQUE constraint can be added without fabricating collisions.
    /// </summary>
    public string IdempotencyKey { get; set; } = null!;

    /// <summary>
    /// Whether this session counts toward play totals. True at insert time when no minimum listen
    /// duration is configured (PlayCounts:MinQualifyingSeconds == 0, the default — preserves the
    /// historical "every session is a play" behavior); otherwise flipped to true by StopAsync once
    /// the observed listen duration meets the configured threshold. Reconciliation and every read
    /// path treat "play count" as COUNT(Qualified) — never raw row count — so this is the one place
    /// the qualification rule lives.
    /// </summary>
    public bool Qualified { get; set; }

    /// <summary>
    /// Hashed (never raw) anonymous-listener identity, present only when UserId is null. Backs
    /// anonymous idempotency (replacing the old per-instance IMemoryCache rate limit, which lost
    /// state on restart and did not share state across replicas) and unique-listener counting.
    /// </summary>
    public string? AnonymousKey { get; set; }
}
