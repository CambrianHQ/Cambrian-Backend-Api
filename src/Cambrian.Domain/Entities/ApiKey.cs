namespace Cambrian.Domain.Entities;

/// <summary>
/// API key for programmatic access. Only the SHA-256 hash is persisted — the raw key
/// is returned exactly once at creation and never stored.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; }

    /// <summary>FK to AspNetUsers.Id (Identity string PK).</summary>
    public string UserId { get; set; } = "";

    /// <summary>Hex-encoded SHA-256 hash of the raw key. Never store the raw key.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>First 8 characters of the raw key (e.g. "cbr_a1b2") — safe to display in dashboards.</summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>Human-readable label chosen by the user (e.g. "My App", "CI Pipeline").</summary>
    public string Name { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public bool IsActive { get; set; } = true;

    // ── Navigation ──
    public ApplicationUser User { get; set; } = null!;
}
