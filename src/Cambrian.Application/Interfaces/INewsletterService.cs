namespace Cambrian.Application.Interfaces;

/// <summary>
/// Persists newsletter opt-ins. Provider sync (Resend/etc.) is a separate follow-up
/// job — this service only owns the durable subscriber record, so the subscribe
/// endpoint never fails because an external email provider is unavailable.
/// </summary>
public interface INewsletterService
{
    Task<NewsletterSubscribeResult> SubscribeAsync(string email, string? source, CancellationToken ct = default);
}

/// <param name="AlreadySubscribed">True when the email was already on the list (idempotent no-op).</param>
public sealed record NewsletterSubscribeResult(bool AlreadySubscribed);
