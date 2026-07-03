namespace Cambrian.Application.DTOs.Newsletter;

/// <summary>Request body for POST /api/newsletter.</summary>
public sealed class NewsletterSubscribeRequest
{
    public string? Email { get; set; }

    /// <summary>Optional signup origin (e.g. "footer", "pricing").</summary>
    public string? Source { get; set; }
}
