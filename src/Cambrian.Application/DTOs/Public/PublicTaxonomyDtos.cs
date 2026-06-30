namespace Cambrian.Application.DTOs.Public;

/// <summary>Public genre entry with a real track count.</summary>
public sealed class PublicGenreDto : PublicSeoResource
{
    /// <summary>Display name of the genre.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URL-safe slug of the genre.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Number of public tracks in this genre (real count).</summary>
    public int TrackCount { get; set; }
}

/// <summary>Public genre detail: the genre plus a page of its public tracks.</summary>
public sealed class PublicGenreDetailDto : PublicSeoResource
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int TrackCount { get; set; }

    /// <summary>A page of public tracks in this genre.</summary>
    public PublicListResponse<PublicTrackDto> Tracks { get; set; } = new();

    /// <summary>Other genres a consumer may want to explore.</summary>
    public List<PublicGenreDto> RelatedGenres { get; set; } = new();
}
