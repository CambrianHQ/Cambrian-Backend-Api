namespace Cambrian.Application.DTOs.Catalog;

public class TrackResponse
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Cambrian track identifier (CAMB-TRK-XXXX).</summary>
    public string CambrianTrackId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Alias for Title — backward compatibility with frontends expecting 'name'.</summary>
    public string Name => Title;

    public string? Description { get; set; }

    public string Genre { get; set; } = string.Empty;

    public string? PrimaryGenre { get; set; }

    public string? Subgenre { get; set; }

    /// <summary>Mood tag for search filtering (e.g. happy, dark, chill, energetic).</summary>
    public string? Mood { get; set; }

    /// <summary>Tempo description or BPM value.</summary>
    public string? Tempo { get; set; }

    /// <summary>User-defined tags for discoverability.</summary>
    public ICollection<string> Tags { get; set; } = new List<string>();

    /// <summary>Whether the track is instrumental (no vocals).</summary>
    public bool Instrumental { get; set; }

    /// <summary>Track visibility: public, limited, hidden.</summary>
    public string Visibility { get; set; } = "public";

    public decimal Price { get; set; }

    public decimal NonExclusivePrice { get; set; }

    public decimal ExclusivePrice { get; set; }

    public decimal CopyrightBuyoutPrice { get; set; }

    /// <summary>Platform fee rate applied to each sale (0.15 = 15%).</summary>
    public decimal PlatformFeePercent { get; set; } = 0.15m;

    /// <summary>Platform fee in dollars for the non-exclusive price.</summary>
    public decimal NonExclusivePlatformFee { get; set; }

    /// <summary>Creator earnings in dollars for a non-exclusive sale.</summary>
    public decimal NonExclusiveCreatorEarnings { get; set; }

    /// <summary>Platform fee in dollars for the exclusive price.</summary>
    public decimal ExclusivePlatformFee { get; set; }

    /// <summary>Creator earnings in dollars for an exclusive sale.</summary>
    public decimal ExclusiveCreatorEarnings { get; set; }

    /// <summary>Platform fee in dollars for the copyright buyout price.</summary>
    public decimal CopyrightBuyoutPlatformFee { get; set; }

    /// <summary>Creator earnings in dollars for a copyright buyout sale.</summary>
    public decimal CopyrightBuyoutCreatorEarnings { get; set; }

    public bool ExclusiveSold { get; set; }

    /// <summary>Track availability status: available, exclusive_sold, copyright_transferred.</summary>
    public string Status { get; set; } = "available";

    /// <summary>Whether the copyright has been transferred via buyout (true = no longer available).</summary>
    public bool IsCopyrightTransferred { get; set; }

    public string? LicenseType { get; set; }

    public string? Duration { get; set; }

    public string? AudioUrl { get; set; }

    public string? CoverArtUrl { get; set; }

    public string CreatorId { get; set; } = string.Empty;

    /// <summary>Creator's public storefront slug (for linking to /creator/{slug}).</summary>
    public string? CreatorSlug { get; set; }

    /// <summary>Creator's profile image URL.</summary>
    public string? CreatorProfileImageUrl { get; set; }

    public string? Artist { get; set; }

    public DateTime CreatedAt { get; set; }
}
