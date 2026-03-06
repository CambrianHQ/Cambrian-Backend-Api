namespace Cambrian.Api.Entities;

public class User
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Role { get; set; } = "User";

    public string Tier { get; set; } = "free";

    public bool VerifiedCreator { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
