using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.Catalog;

public class EditTrackRequest
{
    [MaxLength(200)]
    [SafeMetadata]
    public string? Title { get; set; }

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

    [MaxLength(50)]
    [SafeMetadata]
    public string? Mood { get; set; }

    [MaxLength(30)]
    [SafeMetadata]
    public string? Tempo { get; set; }

    [MaxLength(500)]
    [SafeMetadata]
    public string? Tags { get; set; }

    [Range(1, int.MaxValue)]
    public int? NonExclusivePriceCents { get; set; }

    [Range(1, int.MaxValue)]
    public int? ExclusivePriceCents { get; set; }

    [Range(1, int.MaxValue)]
    public int? CopyrightBuyoutPriceCents { get; set; }
}
