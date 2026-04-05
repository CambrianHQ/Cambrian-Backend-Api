namespace Cambrian.Domain.Entities;

public class ApiKey
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = null!;

    /// <summary>SHA-256 hash of the API key. The raw key is only returned once at creation.</summary>
    public string KeyHash { get; set; } = null!;

    /// <summary>Last 4 characters of the raw key for display purposes.</summary>
    public string KeySuffix { get; set; } = null!;

    public string Name { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public bool IsActive { get; set; } = true;

    public int RateLimit { get; set; } = 1000;

    public ApplicationUser User { get; set; } = null!;
}
