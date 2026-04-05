namespace Cambrian.Domain.Entities;

public class SearchQuery
{
    public Guid Id { get; set; }

    public string Query { get; set; } = null!;

    /// <summary>JSON string of applied filters.</summary>
    public string? Filters { get; set; }

    public int ResultCount { get; set; }

    public string? UserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? SessionId { get; set; }
}
