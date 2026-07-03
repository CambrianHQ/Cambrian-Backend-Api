namespace Cambrian.Domain.Entities;

/// <summary>
/// A newsletter opt-in. Written by POST /api/newsletter. The email is stored
/// normalized (trimmed, lower-cased) with a unique index so re-submitting the same
/// address is idempotent. <see cref="ProviderSynced"/> stays false until a follow-up
/// job pushes the address to the email provider — the subscribe endpoint never blocks
/// on (or fails because of) the provider.
/// </summary>
public class NewsletterSubscriber
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Normalized (trimmed, lower-cased) email address. Unique.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Where the signup came from (e.g. "footer", "pricing"). Optional.</summary>
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>False until the address has been synced to the email provider (retry job).</summary>
    public bool ProviderSynced { get; set; }
}
