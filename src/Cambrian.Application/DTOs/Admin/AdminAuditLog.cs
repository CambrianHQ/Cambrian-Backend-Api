namespace Cambrian.Application.DTOs.Admin;

public class AdminAuditLog
{
    public string Id { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Admin { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    public string? Details { get; set; }
}
