namespace Cambrian.Application.DTOs.Public;

/// <summary>Public FAQ document for SEO / AI question answering.</summary>
public sealed class PublicFaqDto : PublicSeoResource
{
    public List<PublicFaqItemDto> Items { get; set; } = new();
}

/// <summary>A single FAQ question/answer pair.</summary>
public sealed class PublicFaqItemDto
{
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;

    /// <summary>Grouping category (e.g. "Pricing", "Provenance").</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Stable anchor/slug for deep-linking.</summary>
    public string Anchor { get; set; } = string.Empty;
}

/// <summary>
/// A public informational content page (e.g. Release Ready, Authorship, Creator Guide).
/// Structured into sections so AI tools can quote individual parts.
/// </summary>
public sealed class PublicContentPageDto : PublicSeoResource
{
    /// <summary>URL-safe slug for the page.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Primary headline.</summary>
    public string Headline { get; set; } = string.Empty;

    /// <summary>One-paragraph summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Ordered content sections.</summary>
    public List<PublicContentSectionDto> Sections { get; set; } = new();
}

/// <summary>A heading + body block within a content page.</summary>
public sealed class PublicContentSectionDto
{
    public string Heading { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>Stable anchor/slug for deep-linking.</summary>
    public string Anchor { get; set; } = string.Empty;
}
