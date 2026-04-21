using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.V1;

/// <summary>
/// Public v1 license purchase payload. Mirrors the internal CheckoutRequest
/// but versioned separately so the public contract can evolve independently.
/// </summary>
public sealed class LicensePurchaseRequest
{
    /// <summary>UUID or CambrianTrackId (CAMB-TRK-XXXX) of the track to license.</summary>
    [Required]
    public string TrackId { get; set; } = string.Empty;

    /// <summary>One of: non-exclusive, exclusive, copyright_buyout.</summary>
    [Required]
    [RegularExpression("^(non-exclusive|exclusive|copyright_buyout)$",
        ErrorMessage = "licenseType must be 'non-exclusive', 'exclusive', or 'copyright_buyout'.")]
    public string LicenseType { get; set; } = "non-exclusive";

    /// <summary>Optional usage type recorded on the license certificate (personal, youtube, ads, podcast, ...).</summary>
    public string? UsageType { get; set; }

    /// <summary>
    /// Optional client-supplied reference passed through to Stripe. Useful for
    /// reconciling the purchase against an order id in your system.
    /// </summary>
    public string? ClientReferenceId { get; set; }
}

/// <summary>
/// Returned from POST /api/v1/licenses/purchase. Stripe-hosted checkout URL
/// the user is redirected to to complete payment.
/// </summary>
public sealed record LicensePurchaseResponse
{
    public required string CheckoutUrl { get; init; }
    public string? Status { get; init; }
    /// <summary>True when the response was served from the idempotency cache (same Idempotency-Key).</summary>
    public bool Idempotent { get; init; }
}

/// <summary>Returned from GET /api/v1/licenses/{id}/verify.</summary>
public sealed record LicenseVerifyResponse
{
    public required string LicenseId { get; init; }
    public required string TrackId { get; init; }
    public required string LicenseType { get; init; }
    public string? UsageType { get; init; }
    public string? BuyerId { get; init; }
    public DateTime IssuedAt { get; init; }
    public bool Valid { get; init; } = true;
}
