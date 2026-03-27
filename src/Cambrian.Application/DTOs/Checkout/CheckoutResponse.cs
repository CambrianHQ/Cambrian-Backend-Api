namespace Cambrian.Application.DTOs.Checkout;

public class CheckoutResponse
{
    public string CheckoutUrl { get; set; } = string.Empty;

    public string Status { get; set; } = "created";

    /// <summary>Human-readable price, e.g. "$29.99".</summary>
    public string? DisplayPrice { get; set; }

    /// <summary>ISO 4217 currency code, e.g. "usd".</summary>
    public string Currency { get; set; } = "usd";

    /// <summary>
    /// License certificate, populated after payment completion.
    /// </summary>
    public Licenses.LicenseCertificateDto? LicenseCertificate { get; set; }
}