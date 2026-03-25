namespace Cambrian.Application.DTOs.Library;

public class LibraryItemResponse
{
    public string TrackId { get; set; } = string.Empty;

    /// <summary>Alias for TrackId — some frontend components reference "id".</summary>
    public string Id => TrackId;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    /// <summary>True if the user has a completed purchase for this track.</summary>
    public bool Purchased { get; set; }

    /// <summary>ISO-8601 timestamp of when the purchase was completed (null if not purchased).</summary>
    public string? PurchasedOn { get; set; }

    /// <summary>License type of the completed purchase (e.g. "non-exclusive", "exclusive", "copyright_buyout"). Null if not purchased.</summary>
    public string? LicenseType { get; set; }

    /// <summary>Usage type of the completed purchase (e.g. "personal", "youtube", "ads"). Null if not purchased.</summary>
    public string? UsageType { get; set; }

    /// <summary>Audio URL for streaming/download (if entitled).</summary>
    public string? AudioUrl { get; set; }

    /// <summary>Genre of the track.</summary>
    public string? Genre { get; set; }
}
