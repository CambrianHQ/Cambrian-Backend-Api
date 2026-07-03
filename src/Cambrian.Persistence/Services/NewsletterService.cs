using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Services;

/// <summary>
/// Stores newsletter opt-ins in the <c>NewsletterSubscribers</c> table. Emails are
/// normalized (trimmed, lower-cased) and the unique index makes re-subscribing a
/// no-op. Provider sync is deferred to a follow-up job (ProviderSynced = false).
/// </summary>
public sealed class NewsletterService : INewsletterService
{
    private readonly CambrianDbContext _db;

    public NewsletterService(CambrianDbContext db) => _db = db;

    public async Task<NewsletterSubscribeResult> SubscribeAsync(string email, string? source, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();

        // Idempotent: an existing subscriber is a successful no-op.
        if (await _db.NewsletterSubscribers.AnyAsync(n => n.Email == normalized, ct))
            return new NewsletterSubscribeResult(AlreadySubscribed: true);

        _db.NewsletterSubscribers.Add(new NewsletterSubscriber
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
            CreatedAt = DateTime.UtcNow,
            ProviderSynced = false,
        });

        try
        {
            await _db.SaveChangesAsync(ct);
            return new NewsletterSubscribeResult(AlreadySubscribed: false);
        }
        catch (DbUpdateException)
        {
            // Unique-index race: a concurrent request inserted the same email first.
            // Idempotent success rather than an error.
            return new NewsletterSubscribeResult(AlreadySubscribed: true);
        }
    }
}
