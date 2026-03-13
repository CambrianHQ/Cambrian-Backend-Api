namespace Cambrian.Application.DTOs.Licenses;

/// <summary>
/// License certificate issued when a buyer purchases a track.
/// </summary>
public class LicenseCertificateDto
{
    /// <summary>Unique license identifier.</summary>
    public string LicenseId { get; set; } = string.Empty;

    /// <summary>Cambrian track identifier (CAMB-TRK-XXXX).</summary>
    public string TrackId { get; set; } = string.Empty;

    /// <summary>License type (standard, non-exclusive, exclusive).</summary>
    public string LicenseType { get; set; } = string.Empty;

    /// <summary>User ID of the buyer.</summary>
    public string BuyerId { get; set; } = string.Empty;

    /// <summary>User ID of the track creator.</summary>
    public string CreatorId { get; set; } = string.Empty;

    /// <summary>Intended usage context (optional).</summary>
    public string? UsageType { get; set; }

    /// <summary>When the license was issued.</summary>
    public DateTime IssuedAt { get; set; }

    /// <summary>
    /// Specific permitted uses. Null means unrestricted for the license tier.
    /// </summary>
    public List<string>? AllowedUses { get; set; }

    /// <summary>
    /// Usage restrictions or limitations.
    /// </summary>
    public List<string>? Restrictions { get; set; }

    /// <summary>
    /// User ID of the copyright owner. Buyer for copyright_buyout, creator otherwise.
    /// </summary>
    public string? CopyrightOwner { get; set; }
}
