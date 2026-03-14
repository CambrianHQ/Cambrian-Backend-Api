namespace Cambrian.Domain.Entities;

public class StripeWebhookEvent
{
    public Guid Id { get; set; }

    public string EventId { get; set; } = "";

    public string EventType { get; set; } = "";

    /// <summary>Whether the event was fully processed (purchase fulfilled, library updated, etc.).</summary>
    public bool Processed { get; set; }

    /// <summary>Raw JSON payload from Stripe — kept for debugging / dead-letter replay.</summary>
    public string? Payload { get; set; }

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
