namespace Cambrian.Application.DTOs.Checkout;

public class CheckoutRequest
{
    public string TrackId { get; set; } = string.Empty;

    public string LicenseType { get; set; } = "standard";

    public string? ClientReferenceId { get; set; }
}