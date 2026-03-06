namespace Cambrian.Application.DTOs.Admin;

public class AdminUser
{
    public string Id { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Role { get; set; } = "User";

    public string Status { get; set; } = "active";

    public string Tier { get; set; } = "free";

    public bool VerifiedCreator { get; set; }
}
