namespace Cambrian.Application.DTOs.DataIntegrity;

public class DataIntegrityReport
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public int TotalViolations => Violations.Count;

    public bool IsHealthy => TotalViolations == 0;

    public Dictionary<string, int> SummaryByCategory { get; set; } = new();

    public List<IntegrityViolation> Violations { get; set; } = new();
}

public class IntegrityViolation
{
    public string Category { get; set; } = "";

    public string Severity { get; set; } = "warning"; // info, warning, critical

    public string Description { get; set; } = "";

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public Dictionary<string, object?> Details { get; set; } = new();
}
