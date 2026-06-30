namespace Cambrian.Application.DTOs.Public;

/// <summary>
/// Machine-readable sitemap entries for crawlers / MCP. Each entry is a canonical
/// public URL with a last-modified hint. Capped to a safe maximum by the service.
/// </summary>
public sealed class PublicSitemapDto
{
    public List<PublicSitemapEntryDto> Entries { get; set; } = new();

    /// <summary>Total number of entries returned.</summary>
    public int Count { get; set; }
}

/// <summary>A single sitemap entry (mirrors sitemap.xml semantics).</summary>
public sealed class PublicSitemapEntryDto
{
    /// <summary>Absolute canonical URL (sitemap &lt;loc&gt;).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Last modified timestamp (sitemap &lt;lastmod&gt;), when known.</summary>
    public DateTime? LastModified { get; set; }

    /// <summary>Change frequency hint (sitemap &lt;changefreq&gt;).</summary>
    public string ChangeFrequency { get; set; } = "weekly";

    /// <summary>Crawl priority hint (sitemap &lt;priority&gt;), 0.0–1.0.</summary>
    public double Priority { get; set; } = 0.5;

    /// <summary>Resource type: "page", "track", or "creator".</summary>
    public string Type { get; set; } = "page";
}
