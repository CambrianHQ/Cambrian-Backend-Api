using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class UpdateDisplayNameRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;
}
