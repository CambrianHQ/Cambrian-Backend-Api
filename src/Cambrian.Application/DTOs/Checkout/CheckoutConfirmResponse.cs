namespace Cambrian.Application.DTOs.Checkout;

public class CheckoutConfirmResponse
{
    public string Status { get; set; } = "pending";  // paid, pending, failed
    public string? TrackId { get; set; }
    public string? LicenseType { get; set; }
    public bool AddedToLibrary { get; set; }
    public string SessionId { get; set; } = "";
}
