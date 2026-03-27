namespace Cambrian.Application.DTOs.Auth;

public class AuthResponse
{
    public Guid UserId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string Tier { get; set; } = "free";

    public string Role { get; set; } = "User";

    /// <summary>The user's chosen username (null/email-prefix if not yet set).</summary>
    public string? Username { get; set; }

    /// <summary>Phone number for account recovery.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>True when the user has not yet set a custom username (username still equals email).</summary>
    public bool IsNewUser { get; set; }
}