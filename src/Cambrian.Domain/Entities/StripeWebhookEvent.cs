namespace Cambrian.Domain.Entities;

public class StripeWebhookEvent
{
    public string EventId { get; set; } = "";

    public string EventType { get; set; } = "";

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
