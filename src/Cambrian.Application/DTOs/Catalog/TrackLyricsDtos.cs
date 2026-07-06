using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Catalog;

/// <summary>Public lyrics payload for a track.</summary>
public class TrackLyricsDto
{
    public string TrackId { get; set; } = "";
    public string Lyrics { get; set; } = "";
    public string Language { get; set; } = "en";
    public bool? IsExplicit { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Creator upsert for track lyrics. Sending empty/whitespace lyrics removes
/// the lyrics row entirely (lyrics are optional, never required).
/// </summary>
public class UpsertTrackLyricsRequest
{
    [StringLength(20000)]
    public string? Lyrics { get; set; }

    /// <summary>BCP-47 language tag; defaults to "en".</summary>
    [StringLength(16)]
    public string? Language { get; set; }

    /// <summary>Explicit content marker. Null leaves the flag unset/unchanged on delete-then-recreate.</summary>
    public bool? IsExplicit { get; set; }
}
