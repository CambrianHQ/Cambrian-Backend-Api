using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class ResetPasswordRequest
{
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }

    [Required]
    [StringLength(8, MinimumLength = 8)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    public string NewPassword { get; set; } = string.Empty;
}
