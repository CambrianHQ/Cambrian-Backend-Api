namespace Cambrian.Api.Entities;

public class Purchase
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid TrackId { get; set; }

    public string LicenseType { get; set; } = "non-exclusive";

    public decimal Amount { get; set; }

    public string StripeSessionId { get; set; } = string.Empty;

    public bool Paid { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
