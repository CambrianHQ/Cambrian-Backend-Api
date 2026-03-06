namespace Cambrian.Domain.Entities;

public class AuditLog
{
    public Guid Id { get; set; }

    public string Action { get; set; } = "";

    public string Admin { get; set; } = "";

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string? Details { get; set; }
}
