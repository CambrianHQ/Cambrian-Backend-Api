namespace Cambrian.Application.AI.Discovery.Queries;

public class GetCreatorProfileQuery
{
    /// <summary>Creator username, slug, or user ID.</summary>
    public string Identifier { get; set; } = string.Empty;
}
