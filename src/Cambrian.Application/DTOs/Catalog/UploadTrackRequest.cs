using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Cambrian.Application.DTOs.Catalog;

public class UploadTrackRequest
{
    public IFormFile? Audio { get; set; }

    /// <summary>Optional cover art image (JPEG, PNG, WebP; max 10 MB).</summary>
    public IFormFile? CoverArt { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(60)]
    public string? Genre { get; set; }

    [MaxLength(60)]
    public string? PrimaryGenre { get; set; }

    [MaxLength(60)]
    public string? Subgenre { get; set; }

    public decimal? Price { get; set; }

    [MaxLength(200)]
    public string? LicenseType { get; set; }

    [MaxLength(500)]
    public string? Tags { get; set; }

    public decimal? NonExclusivePrice { get; set; }

    public decimal? ExclusivePrice { get; set; }

    public decimal? CopyrightBuyoutPrice { get; set; }

    [MaxLength(20)]
    public string? AlbumAssignmentType { get; set; }

    public Guid? CollectionId { get; set; }

    [MaxLength(200)]
    public string? NewAlbumTitle { get; set; }

    [MaxLength(2000)]
    public string? NewAlbumDescription { get; set; }

    public string? CreatorId { get; set; }
}
