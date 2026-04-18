using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Catalog;

public class EditTrackRequest
{
    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(60)]
    public string? Genre { get; set; }

    [MaxLength(60)]
    public string? PrimaryGenre { get; set; }

    [MaxLength(60)]
    public string? Subgenre { get; set; }

    [MaxLength(50)]
    public string? Mood { get; set; }

    [MaxLength(30)]
    public string? Tempo { get; set; }

    [MaxLength(500)]
    public string? Tags { get; set; }

    [Range(1, int.MaxValue)]
    public int? NonExclusivePriceCents { get; set; }

    [Range(1, int.MaxValue)]
    public int? ExclusivePriceCents { get; set; }

    [Range(1, int.MaxValue)]
    public int? CopyrightBuyoutPriceCents { get; set; }
}
