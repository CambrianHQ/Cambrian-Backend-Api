namespace Cambrian.Domain.Entities;

public sealed class ModerationAction
{
    public Guid Id { get; set; }
    public Guid TargetId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
