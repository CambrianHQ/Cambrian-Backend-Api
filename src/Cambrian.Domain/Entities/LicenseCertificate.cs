namespace Cambrian.Domain.Entities;

/// <summary>
/// Represents a license certificate issued when a buyer purchases a track.
/// </summary>
public class LicenseCertificate
{
    public Guid Id { get; set; }

    /// <summary>The Cambrian track identifier (CAMB-TRK-XXXX).</summary>
    public string TrackId { get; set; } = "";

    /// <summary>User ID of the buyer.</summary>
    public string BuyerId { get; set; } = "";

    public ApplicationUser Buyer { get; set; } = null!;

    /// <summary>User ID of the track creator.</summary>
    public string CreatorId { get; set; } = "";

    public ApplicationUser Creator { get; set; } = null!;

    /// <summary>The purchase that generated this certificate.</summary>
    public Guid PurchaseId { get; set; }

    public Purchase Purchase { get; set; } = null!;

    /// <summary>License type (standard, non-exclusive, exclusive).</summary>
    public string LicenseType { get; set; } = "non-exclusive";

    /// <summary>Intended usage context (personal, youtube, ads, podcast, game, film, social).</summary>
    public string UsageType { get; set; } = "personal";

    /// <summary>When the certificate was issued.</summary>
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Permitted uses (JSON-stored list). Null = unrestricted for tier.</summary>
    public List<string>? AllowedUses { get; set; }

    /// <summary>Restrictions or limitations (JSON-stored list).</summary>
    public List<string>? Restrictions { get; set; }
}
