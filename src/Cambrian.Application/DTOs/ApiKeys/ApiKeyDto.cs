namespace Cambrian.Application.DTOs.ApiKeys;

public class ApiKeyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string KeyMasked { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsActive { get; set; }
    public int RateLimit { get; set; }
}

public class ApiKeyCreatedDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Key { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public int RateLimit { get; set; }
}

public class CreateApiKeyRequest
{
    public string Name { get; set; } = null!;
    public int RateLimit { get; set; } = 1000;
}
