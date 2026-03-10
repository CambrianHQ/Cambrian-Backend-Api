using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class ChangeEmailRequest
{
    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string NewEmail { get; set; } = string.Empty;
}