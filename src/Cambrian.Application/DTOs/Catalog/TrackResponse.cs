namespace Cambrian.Application.DTOs.Catalog;

public class TrackResponse
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Cambrian track identifier (CAMB-TRK-XXXX).</summary>
    public string CambrianTrackId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Genre { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public decimal NonExclusivePrice { get; set; }

    public decimal ExclusivePrice { get; set; }

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

    public bool ExclusiveSold { get; set; }

    public string? LicenseType { get; set; }

    public string? Duration { get; set; }

    public string? AudioUrl { get; set; }

    public string? CoverArtUrl { get; set; }

    public string CreatorId { get; set; } = string.Empty;

    public string? Artist { get; set; }

    public DateTime CreatedAt { get; set; }
}