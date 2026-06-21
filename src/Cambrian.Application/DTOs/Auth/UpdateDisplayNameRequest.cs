using System.ComponentModel.DataAnnotations;
using Cambrian.Application.Validation;

namespace Cambrian.Application.DTOs.Auth;

public class UpdateDisplayNameRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    [SafeMetadata]
    public string DisplayName { get; set; } = string.Empty;
}
