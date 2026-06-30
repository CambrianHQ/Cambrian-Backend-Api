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

    /// <summary>Platform fee rate applied to each sale (0.15 = 15%).</summary>
    public decimal PlatformFeePercent { get; set; } = 0.15m;

    /// <summary>Platform fee in dollars for the non-exclusive price.</summary>
    public decimal NonExclusivePlatformFee { get; set; }

    /// <summary>Creator earnings in dollars for a non-exclusive sale.</summary>
    public decimal NonExclusiveCreatorEarnings { get; set; }

    /// <summary>Track availability status (e.g. available).</summary>
    public string Status { get; set; } = "available";

    public string? Duration { get; set; }

    public string? AudioUrl { get; set; }

    /// <summary>
    /// Whether this track has a streamable audio source. Lets the UI distinguish a
    /// playable track from one whose audio is absent / not yet processed instead of
    /// guessing client-side. True when an audio source is present.
    /// </summary>
    public bool StreamAvailable { get; set; }

    public string? CoverArtUrl { get; set; }

    public string CreatorId { get; set; } = string.Empty;

    /// <summary>Creator's public storefront slug (for linking to /creator/{slug}).</summary>
    public string? CreatorSlug { get; set; }

    /// <summary>Creator's profile image URL.</summary>
    public string? CreatorProfileImageUrl { get; set; }

    public string? Artist { get; set; }

    /// <summary>
    /// Lifetime play count — number of recorded stream sessions for this track
    /// (preview + full plays, including anonymous listeners). Sourced live from
    /// the StreamSessions table; 0 when the track has never been streamed.
    /// </summary>
    public int Plays { get; set; }

    /// <summary>
    /// Number of completed (paid) purchases for this track. Sourced live from the
    /// Purchases table filtered to Status == "completed"; 0 until the track sells.
    /// </summary>
    public int Sales { get; set; }

    /// <summary>
    /// Whether the creator disclosed this track as AI-generated/assisted (DDEX disclosure).
    /// </summary>
    public bool AiGenerated { get; set; }

    /// <summary>
    /// Real provenance state derived from the §9 pipeline: "none" (no hash), "hashed"
    /// (content hash recorded), "stamped" (free ECDSA provenance stamp issued), or
    /// "verified" (stamped + commercial-rights attestation). Never exposes the raw
    /// content hash or signature.
    /// </summary>
    public string ProvenanceStatus { get; set; } = "none";

    public DateTime CreatedAt { get; set; }
}
