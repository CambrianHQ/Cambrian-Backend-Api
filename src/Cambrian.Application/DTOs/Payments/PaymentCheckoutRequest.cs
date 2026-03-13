namespace Cambrian.Application.DTOs.Payments;

public class PaymentCheckoutRequest
{
    public string? TrackId { get; set; }

    public string LicenseType { get; set; } = "non-exclusive";

    public string UsageType { get; set; } = "personal";
}
