namespace Cambrian.Application.DTOs.Subscriptions;

public class SubscriptionResponse
{
    public Guid Id { get; set; }

    public string Plan { get; set; } = "free";

    public string Status { get; set; } = "active";

    public DateTime StartedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? TrialEndsAt { get; set; }
}
