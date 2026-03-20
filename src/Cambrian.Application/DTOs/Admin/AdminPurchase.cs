namespace Cambrian.Application.DTOs.Admin;

public class AdminPurchase
{
    public string Id { get; set; } = string.Empty;

    public string BuyerId { get; set; } = string.Empty;

    public string? BuyerEmail { get; set; }

    public string TrackId { get; set; } = string.Empty;

    public string? TrackTitle { get; set; }

    public int AmountCents { get; set; }

    public string? LicenseType { get; set; }

    public string Status { get; set; } = "pending";

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
