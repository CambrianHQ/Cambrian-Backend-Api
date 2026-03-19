namespace Cambrian.Application.DTOs.Auth;

public class AuthResponse
{
    public Guid UserId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string Token { get; set; } = string.Empty;

    public string Tier { get; set; } = "free";

    public string Role { get; set; } = "User";
}