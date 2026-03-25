using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Auth;

public class SetUsernameRequest
{
    [Required]
    [StringLength(40, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;
}
