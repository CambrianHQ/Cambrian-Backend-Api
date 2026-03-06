namespace Cambrian.Api.Entities;

public class AuditLog
{
    public Guid Id { get; set; }

    public Guid AdminId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public string ResourceId { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Details { get; set; } = string.Empty;
}
