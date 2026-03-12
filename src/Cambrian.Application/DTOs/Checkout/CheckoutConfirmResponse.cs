namespace Cambrian.Application.DTOs.Checkout;

public class CheckoutConfirmResponse
{
    public string Status { get; set; } = "pending";  // paid, pending, failed
    public string? TrackId { get; set; }
    public string? LicenseType { get; set; }
    public bool AddedToLibrary { get; set; }
    public string SessionId { get; set; } = "";

    /// <summary>
    /// License certificate ID issued upon successful purchase.
    /// </summary>
    public string? LicenseId { get; set; }
}
