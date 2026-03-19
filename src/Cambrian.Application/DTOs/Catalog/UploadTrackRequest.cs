using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Cambrian.Application.DTOs.Catalog;

public class UploadTrackRequest
{
    [Required]
    public IFormFile Audio { get; set; } = null!;

    /// <summary>Optional cover art image (JPEG, PNG, WebP; max 10 MB).</summary>
    public IFormFile? CoverArt { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(60)]
    public string? Genre { get; set; }

    public decimal? Price { get; set; }

    public string? LicenseType { get; set; }

    public string? Tags { get; set; }

    public decimal? NonExclusivePrice { get; set; }

    public decimal? ExclusivePrice { get; set; }

    public decimal? CopyrightBuyoutPrice { get; set; }

    public string? CreatorId { get; set; }
}