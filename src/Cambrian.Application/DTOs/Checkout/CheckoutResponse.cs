namespace Cambrian.Application.DTOs.Checkout;

public class CheckoutResponse
{
    public string CheckoutUrl { get; set; } = string.Empty;

    public string Status { get; set; } = "created";

    /// <summary>
    /// License certificate, populated after payment completion.
    /// </summary>
    public Licenses.LicenseCertificateDto? LicenseCertificate { get; set; }
}