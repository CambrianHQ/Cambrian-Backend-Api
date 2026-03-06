namespace Cambrian.Application.DTOs.Purchases;

public class PurchaseResponse
{
    public string Id { get; set; } = "";

    public string TrackId { get; set; } = "";

    public string TrackTitle { get; set; } = "";

    public int AmountCents { get; set; }

    public string Currency { get; set; } = "usd";

    public string LicenseType { get; set; } = "non-exclusive";

    public string Status { get; set; } = "pending";

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
