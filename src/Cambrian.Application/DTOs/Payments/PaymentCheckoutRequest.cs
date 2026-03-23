using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Payments;

public class PaymentCheckoutRequest
{
    [Required]
    public string? TrackId { get; set; }

    [RegularExpression("^(standard|non-exclusive|exclusive|copyright_buyout)$",
        ErrorMessage = "LicenseType must be one of: standard, non-exclusive, exclusive, copyright_buyout")]
    public string LicenseType { get; set; } = "non-exclusive";

    public string UsageType { get; set; } = "personal";
}
