namespace Cambrian.Domain.Entities;

public class Purchase
{
    public Guid Id { get; set; }

    public string BuyerId { get; set; } = "";

    public ApplicationUser Buyer { get; set; } = null!;

    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    /// <summary>Amount in cents (e.g. 2999 = $29.99). Avoids floating-point rounding issues.</summary>
    public int AmountCents { get; set; }

    public string? PaymentMethod { get; set; }

    public string? LicenseType { get; set; }

    public string Status { get; set; } = "pending"; // pending, completed, refunded

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}