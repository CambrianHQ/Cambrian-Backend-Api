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

    public string? CreatorId { get; set; }
}
