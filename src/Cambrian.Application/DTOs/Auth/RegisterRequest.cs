using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class RegisterRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? DisplayName { get; set; }

    /// <summary>Optional role: "creator" to register as a free creator. Defaults to consumer.</summary>
    [RegularExpression("^(user|creator)$", ErrorMessage = "Role must be 'user' or 'creator'.")]
    public string? Role { get; set; }
}