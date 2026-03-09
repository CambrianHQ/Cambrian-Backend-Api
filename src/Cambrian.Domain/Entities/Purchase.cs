namespace Cambrian.Domain.Entities;

public class Purchase
{
    public Guid Id { get; set; }

    public string BuyerId { get; set; } = "";

    public ApplicationUser Buyer { get; set; } = null!;

    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    public double Amount { get; set; }

    public string? PaymentMethod { get; set; }

    public string? StripeSessionId { get; set; }

    public string? LicenseType { get; set; }

    public string Status { get; set; } = "pending"; // pending, completed, refunded

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}