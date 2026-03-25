using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class VerifyCodeRequest
{
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }

    [Required]
    [StringLength(8, MinimumLength = 8)]
    public string Code { get; set; } = string.Empty;
}
