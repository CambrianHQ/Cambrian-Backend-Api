using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class RegisterRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z\d]).{8,}$",
        ErrorMessage = "Password must contain at least one uppercase letter, one lowercase letter, one digit, and one special character.")]
    public string Password { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional role. Accepts "listener" (preferred), legacy "user", or "creator".
    /// Defaults to listener when omitted.
    /// </summary>
    [RegularExpression("(?i)^(listener|user|creator)$", ErrorMessage = "Role must be 'listener' or 'creator'.")]
    public string? Role { get; set; }

    /// <summary>Optional phone number for account recovery and notifications.</summary>
    [Phone]
    public string? PhoneNumber { get; set; }
}
