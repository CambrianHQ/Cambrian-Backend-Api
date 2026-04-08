namespace Cambrian.Domain.Entities;

/// <summary>
/// A single email signup to the public waitlist (issue #72).
///
/// Anonymous, no FK to AspNetUsers — many waitlist signups will come from
/// people who do not (yet) have an account. Email is the natural unique key.
/// </summary>
public class WaitlistSignup
{
    public Guid Id { get; set; }

    /// <summary>Lowercase, trimmed. Unique index in the DbContext config.</summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// Optional source tag (page slug, utm_source, campaign id, etc.) so the
    /// growth team can attribute signups. Capped at 100 chars in the config.
    /// </summary>
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
