using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.CreatorProfile;

/// <summary>
/// One entry on a creator's public "Artist Journey" timeline — a progress
/// update, milestone, rehearsal/studio photo, or upcoming show. Stored as a
/// JSON array string on CreatorProfile (SocialLinks precedent); the list is
/// capped small, and if journey usage outgrows the profile blob it graduates
/// to its own table (ActivityItem/CreatorMilestone precedent) without a
/// contract change. Hidden publicly when empty.
/// </summary>
public class JourneyEntryDto
{
    /// <summary>Entry kind: update | milestone | photo | event.</summary>
    [StringLength(20)]
    public string Type { get; set; } = "update";

    [StringLength(120)]
    [SafeMetadata]
    public string Title { get; set; } = "";

    [StringLength(1000)]
    [SafeMetadata]
    public string? Body { get; set; }

    /// <summary>Optional image (rehearsal/studio photo) — platform-hosted URL.</summary>
    [StringLength(500)]
    public string? ImageUrl { get; set; }

    /// <summary>ISO-8601 date the entry is about (show date, milestone date).</summary>
    [StringLength(30)]
    public string? Date { get; set; }

    /// <summary>Venue / location, free text — used for event entries.</summary>
    [StringLength(200)]
    [SafeMetadata]
    public string? Venue { get; set; }

    /// <summary>Optional external link (tickets, recap post…), http(s) only.</summary>
    [StringLength(500)]
    public string? Link { get; set; }
}
