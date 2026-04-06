namespace Cambrian.Application.DTOs.ApiKeys;

public class ApiKeyListItemDto
{
    public Guid Id { get; set; }
    public string KeyPrefix { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
