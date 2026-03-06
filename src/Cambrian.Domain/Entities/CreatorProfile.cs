namespace Cambrian.Domain.Entities;

public sealed class CreatorProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Bio { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}
