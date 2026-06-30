namespace Cambrian.Application.DTOs.Public;

/// <summary>
/// Base shape for every public, crawlable resource exposed to the read-only MCP
/// server and SEO/AI consumers. Carries only data that is safe to publish:
/// human-readable copy, a canonical URL, a public image URL, a last-modified
/// timestamp, tags, related links, and search/AI metadata. It intentionally
/// contains NO storage keys, emails, Stripe/payment data, or internal flags.
/// </summary>
public abstract class PublicSeoResource
{
    /// <summary>Human-readable title of the resource.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Short, public description suitable for a result snippet.</summary>
    public string? Description { get; set; }

    /// <summary>Absolute, canonical public URL for this resource (no localhost in prod).</summary>
    public string CanonicalUrl { get; set; } = string.Empty;

    /// <summary>Absolute, public, CORS-safe image URL. Never a raw storage key.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Last time this resource changed, when known (used for sitemaps / freshness).</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Free-form public tags for discovery.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Absolute URLs to related public resources (creator, genre, etc.).</summary>
    public List<string> RelatedUrls { get; set; } = new();

    /// <summary>SEO &lt;title&gt; suggestion.</summary>
    public string MetaTitle { get; set; } = string.Empty;

    /// <summary>SEO meta-description suggestion.</summary>
    public string MetaDescription { get; set; } = string.Empty;

    /// <summary>schema.org type hint for structured data (e.g. "MusicRecording", "MusicGroup").</summary>
    public string StructuredDataType { get; set; } = string.Empty;
}

/// <summary>
/// Paged collection envelope for public list endpoints. Carries collection-level
/// SEO metadata plus standard pagination fields. Page size is always clamped by the
/// service to a safe maximum before this is built.
/// </summary>
/// <typeparam name="T">Item DTO type.</typeparam>
public sealed class PublicListResponse<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;

    /// <summary>Canonical URL for this collection (the list endpoint).</summary>
    public string CanonicalUrl { get; set; } = string.Empty;

    public string MetaTitle { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;

    /// <summary>schema.org type for the collection (always "ItemList").</summary>
    public string StructuredDataType { get; set; } = "ItemList";
}
