using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Checkout;

public class CheckoutRequest
{
    [Required]
    public string TrackId { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^(standard|non-exclusive|exclusive)$",
        ErrorMessage = "LicenseType must be standard, non-exclusive, or exclusive.")]
    public string LicenseType { get; set; } = "standard";

    public string? ClientReferenceId { get; set; }
}