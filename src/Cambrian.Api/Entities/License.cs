namespace Cambrian.Api.Entities;

public class License
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsExclusive { get; set; }

    public string Description { get; set; } = string.Empty;
}
