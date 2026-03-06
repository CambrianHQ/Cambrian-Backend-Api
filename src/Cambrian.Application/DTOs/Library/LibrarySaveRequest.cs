using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Library;

public class LibrarySaveRequest
{
    [Required]
    public string TrackId { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Artist { get; set; }

    public string? AudioUrl { get; set; }
}
