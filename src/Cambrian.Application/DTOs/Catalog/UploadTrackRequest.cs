using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Cambrian.Application.DTOs.Catalog;

public class UploadTrackRequest
{
    [Required]
    public IFormFile Audio { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(60)]
    public string? Genre { get; set; }

    public double? Price { get; set; }

    public string? LicenseType { get; set; }

    public string? Tags { get; set; }

    public double? NonExclusivePrice { get; set; }

    public double? ExclusivePrice { get; set; }

    public string? CreatorId { get; set; }
}