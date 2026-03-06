namespace Cambrian.Api.Entities;

public class StripeEvent
{
    public string EventId { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
