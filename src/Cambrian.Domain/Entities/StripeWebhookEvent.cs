namespace Cambrian.Domain.Entities;

public class StripeWebhookEvent
{
    public Guid Id { get; set; }

    public string EventId { get; set; } = "";

    public string EventType { get; set; } = "";

    /// <summary>Whether the event was fully processed (purchase fulfilled, library updated, etc.).</summary>
    public bool Processed { get; set; }

    /// <summary>Processing status: received | processing | completed | failed.</summary>
    public string Status { get; set; } = "received";

    /// <summary>Error message when Status == "failed".</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Raw JSON payload from Stripe — kept for debugging / dead-letter replay.</summary>
    public string? Payload { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
