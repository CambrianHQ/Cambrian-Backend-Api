namespace Cambrian.Application.DTOs.Purchases;

public class PurchaseCreateRequest
{
    public string TrackId { get; set; } = "";

    public string? LicenseType { get; set; }

    public string? PaymentMethod { get; set; }

    /// <summary>
    /// Stripe checkout session ID. Required to verify payment was completed
    /// before creating the purchase record.
    /// </summary>
    public string? StripeSessionId { get; set; }
}
