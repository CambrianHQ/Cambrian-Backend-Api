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

    [MaxLength(50)]
    public string? Mood { get; set; }

    [MaxLength(30)]
    public string? Tempo { get; set; }

    [MaxLength(500)]
    public string? Tags { get; set; }

    public int? NonExclusivePriceCents { get; set; }

    public int? ExclusivePriceCents { get; set; }

    public int? CopyrightBuyoutPriceCents { get; set; }
}
