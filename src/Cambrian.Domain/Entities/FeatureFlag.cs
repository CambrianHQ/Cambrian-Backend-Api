namespace Cambrian.Domain.Entities;

public class FeatureFlag
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>
    /// Percentage of users who see this feature (0–100).
    /// </summary>
    public int RolloutPercentage { get; set; } = 100;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
