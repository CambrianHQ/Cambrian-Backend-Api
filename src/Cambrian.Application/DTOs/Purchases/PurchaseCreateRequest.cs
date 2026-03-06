namespace Cambrian.Application.DTOs.Purchases;

public class PurchaseCreateRequest
{
    public string TrackId { get; set; } = "";

    public string? LicenseType { get; set; }

    public string? PaymentMethod { get; set; }
}
