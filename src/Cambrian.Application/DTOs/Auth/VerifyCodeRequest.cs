using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class VerifyCodeRequest
{
    public string? Email { get; set; }

    public string? PhoneNumber { get; set; }

    [Required]
    [StringLength(6, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}
