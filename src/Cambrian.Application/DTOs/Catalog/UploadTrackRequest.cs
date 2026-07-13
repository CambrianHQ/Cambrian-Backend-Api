using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;
using Microsoft.AspNetCore.Http;

namespace Cambrian.Application.DTOs.Catalog;

public class UploadTrackRequest
{
    public IFormFile? Audio { get; set; }

    /// <summary>Optional cover art image (JPEG, PNG, WebP; max 10 MB).</summary>
    public IFormFile? CoverArt { get; set; }

    [Required]
    [MaxLength(200)]
    [SafeMetadata]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    [SafeMetadata]
    public string? Description { get; set; }

    [MaxLength(60)]
    [SafeMetadata]
    public string? Genre { get; set; }

    [MaxLength(60)]
    [SafeMetadata]
    public string? PrimaryGenre { get; set; }

    [MaxLength(60)]
    [SafeMetadata]
    public string? Subgenre { get; set; }

    /// <summary>Tempo (BPM) — the upload wizard collects this and Release Ready
    /// requires it, so dropping it here forced creators to re-enter it on the
    /// edit page before their first master run.</summary>
    [MaxLength(20)]
    [SafeMetadata]
    public string? Tempo { get; set; }

    /// <summary>Mood — same Release Ready metadata requirement as Tempo.</summary>
    [MaxLength(120)]
    [SafeMetadata]
    public string? Mood { get; set; }

    public decimal? Price { get; set; }

    [MaxLength(200)]
    [SafeMetadata]
    public string? LicenseType { get; set; }

    [MaxLength(500)]
    [SafeMetadata]
    public string? Tags { get; set; }

    [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
    public decimal? NonExclusivePrice { get; set; }

    [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
    public decimal? ExclusivePrice { get; set; }

    [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
    public decimal? CopyrightBuyoutPrice { get; set; }

    /// <summary>Non-exclusive price in cents (preferred over NonExclusivePrice).</summary>
    [Range(1, int.MaxValue)]
    public int? NonExclusivePriceCents { get; set; }

    /// <summary>Exclusive price in cents (preferred over ExclusivePrice).</summary>
    [Range(1, int.MaxValue)]
    public int? ExclusivePriceCents { get; set; }

    /// <summary>Copyright buyout price in cents (preferred over CopyrightBuyoutPrice).</summary>
    [Range(1, int.MaxValue)]
    public int? CopyrightBuyoutPriceCents { get; set; }

    [MaxLength(20)]
    public string? AlbumAssignmentType { get; set; }

    public Guid? CollectionId { get; set; }

    [MaxLength(200)]
    [SafeMetadata]
    public string? NewAlbumTitle { get; set; }

    [MaxLength(2000)]
    [SafeMetadata]
    public string? NewAlbumDescription { get; set; }

    /// <summary>
    /// When true the track is created with Visibility = "hidden" (a draft):
    /// it exists, is playable by its owner, but is not publicly listed until
    /// the creator publishes it. Used by bulk upload so nothing goes live
    /// before the creator finishes metadata.
    /// </summary>
    public bool? SaveAsDraft { get; set; }

    public string? CreatorId { get; set; }

    /// <summary>
    /// Optional fallback transport for the client's Idempotency-Key when the caller
    /// can't set the header directly (e.g. one item inside a batch upload). The
    /// single-track endpoints prefer the "Idempotency-Key" header when both are present.
    /// </summary>
    [MaxLength(128)]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// When true, skips the same-creator/same-audio duplicate-content check
    /// (returned as a "duplicate_audio_detected" failure otherwise). Set by the
    /// client only after the creator explicitly confirms "Upload anyway".
    /// </summary>
    public bool? ConfirmDuplicateAudio { get; set; }
}
