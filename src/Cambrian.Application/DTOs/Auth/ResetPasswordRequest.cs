using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class ResetPasswordRequest
{
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }

    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Code must be a 6-digit number.")]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    public string NewPassword { get; set; } = string.Empty;
}
