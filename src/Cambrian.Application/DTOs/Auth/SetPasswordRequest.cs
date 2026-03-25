using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class SetPasswordRequest
{
    [Required]
    [MinLength(8)]
    public string Password { get; set; } = string.Empty;
}
